using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Application.DependencyInjection;
using Application.Interfaces.ClaudeCode;
using Application.Interfaces.Configuration;
using Application.Interfaces.Security;
using Application.Services.Configuration;
using Core.Configuration;
using Core.Upstream;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.ExternalSources.ClaudeCode;

/// <summary>只更新工程私有配置的顶层 userID 与（照原生登录写的）oauthAccount，保留其余字节；凭据文件和 machineID 不参与身份准备。</summary>
internal sealed class ClaudeConfigurationPreparation(IServiceScopeFactory scopes, IRelayInstance instance, ISecretProtector protector,
    IOwnedClaudeProcesses ownedProcesses) : IClaudeConfigurationPreparation
{
    private const string ConfigName = ".claude.json";
    private const int MaxBytes = 1024 * 1024;
    private readonly object _sync = new();
    /// <summary>
    /// 本代身份下已核对一致的账户。换号只经 <see cref="PrepareExistingAccounts"/>（维护排空后、重新开放准入前），
    /// 登录与导入只经 <see cref="PrepareAccount"/>，两者开头都清空，所以命中即属于当前这一代。
    /// </summary>
    private readonly HashSet<string> _validated = new(StringComparer.Ordinal);
    /// <summary>本代身份下已读出的账户 oauthAccount.accountUuid（按账户主键）。与 <see cref="_validated"/> 同锁、同时清空：PrepareAccount、PrepareExistingAccounts 开头一并失效，正常转发命中即不再打开私有配置。</summary>
    private readonly Dictionary<string, string> _accountUuids = new(StringComparer.Ordinal);

    /// <summary>从设置里读取已有设备编号（唯一一份），缺失或损坏时拒绝，绝不生成替代值。</summary>
    public string DeviceId
    {
        get
        {
            // 官方分支按同步属性取设备编号（本单只换存取、不改官方分支），只能在这里同步等待一次读设置；Microsoft.Data.Sqlite 的异步读在内部同步完成，没有同步上下文，不会死锁。
            var value = ReadDeviceId().GetAwaiter().GetResult();
            if (!RelayConfiguration.IsHex(value, 64)) throw Invalid("站点设备编号缺失或损坏，请恢复完整配套备份。");
            return value;
        }
    }

    /// <summary>经通用仓储读出设置里的设备编号。</summary>
    private async Task<string> ReadDeviceId()
    {
        await using var scope = scopes.CreateAsyncScope();
        return (await scope.Repository<RelaySettings>().LoadSettings()).Snapshot().Value.DeviceId;
    }

    /// <summary>受控准备指定账户；现存账户配置缺失时要求恢复，不把缺损当作新账户。</summary>
    public void PrepareAccount(string accountId, bool allowCreate = false)
    {
        // 同一副本的校验与原子替换串行，防止两个准备操作交错覆盖。
        lock (_sync)
        {
            _validated.Clear(); _accountUuids.Clear();
            var directory = AccountDirectory(accountId);
            var deviceId = DeviceId;
            if (allowCreate && !System.IO.Directory.Exists(directory)) protector.ProtectDirectory(directory);
            Prepare(directory, deviceId, allowCreate);
        }
    }

    /// <summary>普通操作只读核对，不写配置、不读取凭据，也不启动客户端；本代已核对过的账户不再打开文件，<paramref name="reread"/> 时重读。</summary>
    public void ValidateAccount(string accountId, bool reread = false)
    {
        // 与受控准备共锁，读取不会夹在文件校验与发布之间，缓存也不会在清空时被旧结果写回。
        lock (_sync)
        {
            if (!reread && _validated.Contains(accountId)) return;
            _validated.Remove(accountId);
            var directory = AccountDirectory(accountId);
            var bytes = Read(directory, false);
            if (Inspect(bytes).Value != DeviceId) throw Invalid("官方账户的私有设备配置尚未对齐，请在本机管理页完成身份准备。");
            // 顺便缓存本代身份下的账户编号：核对设备时已经读了这份私有配置，这里不再多开一次文件。缺 oauthAccount 或格式不对只是不缓存，真要用时由 AccountUuid 报本地阻断。
            if (ParseAccountUuid(bytes) is { } accountUuid) _accountUuids[accountId] = accountUuid; else _accountUuids.Remove(accountId);
            _validated.Add(accountId);
        }
    }

    /// <summary>
    /// 读出官方账户当前的 oauthAccount.accountUuid（发往官方时把 metadata.user_id 的账户段对齐原生直连用）；本代身份下核对过即命中缓存、不再打开私有配置。
    /// 缺 oauthAccount、不是字符串或不是标准 UUID 时抛 <see cref="ClaudeConfigurationUnavailableException"/>，交给调用方按本地身份阻断处理（宁可不发，不进暂停冷却）。
    /// </summary>
    public string AccountUuid(string accountId)
    {
        // 与受控准备共锁，读取不会夹在文件校验与发布之间。
        lock (_sync)
        {
            if (_accountUuids.TryGetValue(accountId, out var cached)) return cached;
            var directory = AccountDirectory(accountId);
            var accountUuid = ParseAccountUuid(Read(directory, false))
                ?? throw Invalid("这个官方账户还没有账户编号，这次没有发往官方。请在本机管理页的上游页对它按「测试」，测试会向官方取回账户编号；独立 API 上游仍可使用。");
            _accountUuids[accountId] = accountUuid;
            return accountUuid;
        }
    }

    /// <summary>解析私有配置顶层 oauthAccount.accountUuid：是标准 UUID 的字符串才返回，其余（缺失、类型不符、格式不对、损坏）返回 null，不抛也不转述原文。</summary>
    private static string? ParseAccountUuid(byte[] bytes)
    {
        try
        {
            var bom = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
            using var document = JsonDocument.Parse(bytes.AsMemory(bom));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("oauthAccount", out var account) || account.ValueKind != JsonValueKind.Object
                || !account.TryGetProperty("accountUuid", out var value) || value.ValueKind != JsonValueKind.String) return null;
            var accountUuid = value.GetString();
            return Guid.TryParseExact(accountUuid, "D", out _) ? accountUuid : null;
        }
        // 私有配置损坏时不透出其中内容，按「取不到账户编号」处理。
        catch (JsonException) { return null; }
    }

    /// <summary>仅接受系统临时目录下工程创建的独立诊断配置，个人 Claude/MyCoAI 目录不在可写范围。</summary>
    public void PrepareTransient(string configDirectory)
    {
        // 临时目录准备也与账户维护串行，单次读取的站点编号保持一致。
        lock (_sync)
        {
            var directory = Path.GetFullPath(configDirectory);
            var parent = System.IO.Directory.GetParent(directory) ?? throw Invalid("隔离诊断目录无效。");
            var prefix = "HouseholdRelay-diagnostic-";
            if (Path.GetFileName(directory) != "config" || !parent.Name.StartsWith(prefix, StringComparison.Ordinal)
                || !RelayConfiguration.IsHex(parent.Name[prefix.Length..], 32)
                || !string.Equals(parent.Parent?.FullName, Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), StringComparison.OrdinalIgnoreCase))
                throw Invalid("隔离诊断目录不在工程允许的临时范围内。");
            CheckPath(parent.FullName, directory);
            System.IO.Directory.CreateDirectory(directory);
            Prepare(directory, DeviceId, true);
        }
    }

    /// <summary>逐个对齐既有账户，损坏目录立即报出固定原因；已完成的对齐可安全重做。</summary>
    public void PrepareExistingAccounts(CancellationToken cancellation = default)
    {
        // 整批准备共享一份站点编号，维护完成前不允许其他准备穿插。
        lock (_sync)
        {
            _validated.Clear(); _accountUuids.Clear();
            cancellation.ThrowIfCancellationRequested();
            ownedProcesses.EnsureExited();
            var root = Path.Combine(Path.GetFullPath(instance.State), "official-accounts");
            CheckPath(Path.GetFullPath(instance.State), root);
            if (!System.IO.Directory.Exists(root)) return;
            var deviceId = DeviceId;
            foreach (var directory in System.IO.Directory.EnumerateDirectories(root))
            {
                cancellation.ThrowIfCancellationRequested();
                Prepare(AccountDirectory(Path.GetFileName(directory)), deviceId, false, cancellation);
            }
        }
    }

    /// <summary>限定账户主键与路径，并拒绝数据目录和目标之间的链接或重解析点。</summary>
    private string AccountDirectory(string accountId)
    {
        if (accountId.Length != 32 || !accountId.All(char.IsAsciiHexDigitLower)) throw Invalid("官方账户目录名称无效，请检查配套备份。");
        var root = Path.GetFullPath(instance.State);
        var directory = Path.GetFullPath(Path.Combine(root, "official-accounts", accountId));
        CheckPath(root, directory);
        return directory;
    }

    /// <summary>校验并原子更新单个派生副本；File.Replace 保留目的文件 ACL，其余 JSON 字节直接复制。</summary>
    private static void Prepare(string directory, string deviceId, bool allowCreate, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var bytes = Read(directory, allowCreate);
        var location = Inspect(bytes);
        if (location.Value == deviceId) return;
        var replacement = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(deviceId));
        byte[] updated;
        if (location.Offset >= 0)
            updated = [.. bytes.AsSpan(0, location.Offset), .. replacement, .. bytes.AsSpan(location.Offset + location.Length)];
        else
        {
            var property = Encoding.UTF8.GetBytes((location.HasProperties ? "," : "") + "\"userID\":" + JsonSerializer.Serialize(deviceId));
            updated = [.. bytes.AsSpan(0, location.Close), .. property, .. bytes.AsSpan(location.Close)];
        }
        Publish(directory, updated, cancellation);
    }

    /// <summary>
    /// 把官方返回的账户资料照原生登录写进顶层 oauthAccount：账户编号与现值相同时不写文件；没有 oauthAccount 时补在根对象末尾；换了账户（或现值不是可用编号）时整段换成新账户的三项。
    /// 其余字节原样、原子替换保 ACL；与准备、核对共锁，写完同步本代缓存，正常转发随即用新值。
    /// </summary>
    public void RecordAccount(string accountId, OfficialAccountProfile profile)
    {
        if (!Guid.TryParseExact(profile.AccountUuid, "D", out _)) throw Invalid("官方返回的账户编号格式不对，没有保存。");
        // 与准备、核对、读编号共锁：写文件和更新缓存一起完成，转发读不到写了一半的配置，也不会拿到与文件不符的缓存。
        lock (_sync)
        {
            var directory = AccountDirectory(accountId);
            var bytes = Read(directory, false);
            var location = Inspect(bytes);
            if (ParseAccountUuid(bytes) != profile.AccountUuid)
            {
                var value = new JsonObject { ["accountUuid"] = profile.AccountUuid };
                if (profile.EmailAddress is { } email) value["emailAddress"] = email;
                if (profile.OrganizationUuid is { } organization) value["organizationUuid"] = organization;
                var replacement = Encoding.UTF8.GetBytes(value.ToJsonString());
                var (offset, length) = AccountValue(bytes);
                byte[] updated = offset >= 0
                    ? [.. bytes.AsSpan(0, offset), .. replacement, .. bytes.AsSpan(offset + length)]
                    : [.. bytes.AsSpan(0, location.Close), .. Encoding.UTF8.GetBytes(location.HasProperties ? ",\"oauthAccount\":" : "\"oauthAccount\":"), .. replacement, .. bytes.AsSpan(location.Close)];
                Publish(directory, updated);
            }
            _accountUuids[accountId] = profile.AccountUuid;
        }
    }

    /// <summary>顶层 oauthAccount 值（任意类型）在原始字节里的区间，含 BOM 偏移；没有时为 (-1, 0)。调用前已由 <see cref="Inspect"/> 排除重复键与损坏。</summary>
    private static (int Offset, int Length) AccountValue(byte[] bytes)
    {
        var bom = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
        var reader = new Utf8JsonReader(bytes.AsSpan(bom));
        reader.Read();
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1 || !reader.ValueTextEquals("oauthAccount")) continue;
            reader.Read();
            var start = checked((int)reader.TokenStartIndex);
            reader.Skip();
            return (start + bom, checked((int)reader.BytesConsumed) - start);
        }
        return (-1, 0);
    }

    /// <summary>把改好的完整字节写到同目录临时文件，再原子替换私有配置；File.Replace 保留目的文件 ACL，失败时删掉临时文件、原配置不变。</summary>
    private static void Publish(string directory, byte[] updated, CancellationToken cancellation = default)
    {
        var path = Path.Combine(directory, ConfigName);
        var temporary = Path.Combine(directory, ".claude-identity-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            cancellation.ThrowIfCancellationRequested();
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { output.Write(updated); output.Flush(true); }
            cancellation.ThrowIfCancellationRequested();
            CheckPath(directory, path);
            if (File.Exists(path)) File.Replace(temporary, path, null);
            // 只有显式允许的新配置才落到这里；不覆盖另一操作刚创建的文件。
            else File.Move(temporary, path);
            cancellation.ThrowIfCancellationRequested();
        }
        // 本次临时文件只在受控目录内，失败后删除它，不更改原始配置。
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>有界读取配置；仅明确的新目录可以从空 JSON 创建，不读取任何凭据文件。</summary>
    private static byte[] Read(string directory, bool allowCreate)
    {
        var path = Path.Combine(directory, ConfigName);
        CheckPath(directory, path);
        if (!System.IO.Directory.Exists(directory)) throw Invalid("官方账户私有目录缺失，请恢复完整配套备份。");
        if (!File.Exists(path))
        {
            if (allowCreate) return "{}"u8.ToArray();
            throw Invalid("官方账户私有配置缺失，请恢复配套配置后重新准备身份。");
        }
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > MaxBytes) throw Invalid("官方账户私有配置超过允许大小，请检查配套备份。");
            var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
            if (input.ReadByte() != -1) throw Invalid("官方账户私有配置在读取期间改变，请稍后重新准备。");
            return bytes;
        }
        // 别的程序正占着或刚好改写这个文件：归入本地身份阻断，这次不发往官方、可换独立 API、不进认证暂停或冷却；不转述系统原话。
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw Invalid("官方账户私有配置正被其他程序占用或暂时读不到，这次没有发往官方。请稍后重试；独立 API 上游仍可使用。"); }
    }

    /// <summary>解析完整 JSON 并定位唯一顶层 userID 的原字节区间；重复键、错误类型和损坏文件拒绝覆盖。</summary>
    private static Location Inspect(byte[] bytes)
    {
        try
        {
            var bom = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
            var reader = new Utf8JsonReader(bytes.AsSpan(bom));
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) throw new JsonException();
            var objects = new Stack<HashSet<string>>(); objects.Push(new(StringComparer.Ordinal));
            var offset = -1; var length = 0; var close = -1; var hasProperties = false; string? value = null;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.StartObject) objects.Push(new(StringComparer.Ordinal));
                else if (reader.TokenType == JsonTokenType.EndObject)
                { objects.Pop(); if (reader.CurrentDepth == 0) close = checked((int)reader.TokenStartIndex + bom); }
                else if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    var name = reader.GetString()!;
                    if (!objects.Peek().Add(name)) throw new JsonException();
                    if (reader.CurrentDepth != 1) continue;
                    hasProperties = true;
                    if (name != "userID") continue;
                    if (!reader.Read() || reader.TokenType != JsonTokenType.String) throw new JsonException();
                    offset = checked((int)reader.TokenStartIndex + bom); length = checked((int)reader.BytesConsumed + bom - offset); value = reader.GetString();
                }
            }
            if (close < 0 || objects.Count != 0) throw new JsonException();
            return new(offset, length, close, hasProperties, value);
        }
        // 不转述解析器原话，避免把私有配置中的其他内容带进页面或日志。
        catch (JsonException) { throw Invalid("官方账户私有配置损坏、字段重复或 userID 类型不支持，请恢复配套配置后重新准备身份。"); }
    }

    /// <summary>检查路径保持在明确根目录内，并拒绝沿途已有的符号链接或重解析点。</summary>
    private static void CheckPath(string root, string path)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)); path = Path.GetFullPath(path);
        if (path != root && !path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw Invalid("私有配置路径超出工程范围。");
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw Invalid("私有配置路径含链接或重解析点，未更改任何配置。请恢复工程自有目录。");
            }
            // 尚未创建的路径可以继续检查父目录；不能因 Exists 跟随断链而漏掉重解析点。
            catch (FileNotFoundException) { }
            // 新账户的父层可能尚不存在，仍检查更上层的工程数据目录。
            catch (DirectoryNotFoundException) { }
        }
    }

    /// <summary>配置准备错误只给固定可操作说明，不附原始 JSON 或设备值。</summary>
    private static ClaudeConfigurationUnavailableException Invalid(string message) => new(message);

    /// <summary>顶层设备值和根对象结束位置；偏移针对包含 BOM 的原始字节。</summary>
    private sealed record Location(int Offset, int Length, int Close, bool HasProperties, string? Value);
}
