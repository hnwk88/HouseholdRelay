using System.Security.AccessControl;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Infrastructure.ExternalSources.ClaudeCode;
using static Tests.TestSuite;

namespace Tests;

/// <summary>工程私有 Claude 配置的受控准备：只改 userID、保留字节和 ACL，并在续期前拒绝不一致身份。</summary>
static class ClaudeConfigurationTests
{
    private const string AccountId = "1234567890abcdef1234567890abcdef";

    /// <summary>带 BOM、空白、转义、嵌套字段的配置只替换设备字符串；重复准备和校验不写文件，凭据及 ACL 原样保持。</summary>
    public static async Task PreserveBytesAndAcl()
    {
        await using var web = await WebFixture.Create();
        var preparation = web.App.Services.GetRequiredService<IClaudeConfigurationPreparation>();
        var directory = DirectoryFor(web); WindowsSecrets.ProtectDirectory(directory);
        var path = Path.Combine(directory, ".claude.json");
        var old = new string('a', 64);
        var text = "{\r\n  \"machineID\": \"machine-untouched\", \"userID\" : \"" + old + "\",\r\n  \"unknown\": {\"escaped\":\"\\u4e2d\\/\\\\\", \"userID\":\"nested-untouched\"}, \"list\": [1, 2]\r\n}\r\n";
        byte[] original = [0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes(text)]; File.WriteAllBytes(path, original);
        var credentialPath = Path.Combine(directory, OfficialCredential.FileName);
        var credential = FakeClaude.Credential("fixture-private-preserved-token", -1); File.WriteAllBytes(credentialPath, credential);
        var acl = new FileInfo(path).GetAccessControl().GetSecurityDescriptorBinaryForm();
        preparation.PrepareAccount(AccountId);
        var expected = new byte[] { 0xef, 0xbb, 0xbf }.Concat(Encoding.UTF8.GetBytes(text.Replace(old, preparation.DeviceId, StringComparison.Ordinal))).ToArray();
        Check(File.ReadAllBytes(path).SequenceEqual(expected), "Only the exact top-level userID string may change");
        Check(File.ReadAllBytes(credentialPath).SequenceEqual(credential), "Credential bytes must stay unchanged");
        Check(new FileInfo(path).GetAccessControl().GetSecurityDescriptorBinaryForm().SequenceEqual(acl), "Atomic replacement must preserve ACL");
        var modified = File.GetLastWriteTimeUtc(path);
        preparation.PrepareExistingAccounts(); preparation.ValidateAccount(AccountId);
        Check(File.GetLastWriteTimeUtc(path) == modified && File.ReadAllBytes(path).SequenceEqual(expected), "Repeated preparation and validation are read-only");
    }

    /// <summary>缺失、重复键、损坏 JSON 和错误类型均保持原文件；取消的整批准备不得写第一个账户。</summary>
    public static async Task RefuseDamagedConfiguration()
    {
        await using var web = await WebFixture.Create();
        var preparation = web.App.Services.GetRequiredService<IClaudeConfigurationPreparation>();
        var directory = DirectoryFor(web); WindowsSecrets.ProtectDirectory(directory);
        var path = Path.Combine(directory, ".claude.json");
        Refused(() => preparation.PrepareAccount(AccountId));
        Check(!File.Exists(path), "Missing existing configuration must not be silently recreated");
        foreach (var invalid in new[] { "{", "[]", "{\"userID\":null}", "{\"userID\":1}", "{\"userID\":\"one\",\"userID\":\"two\"}", "{\"unknown\":{\"x\":1,\"x\":2}}" })
        {
            File.WriteAllText(path, invalid); var before = File.ReadAllBytes(path);
            Refused(() => preparation.PrepareAccount(AccountId)); Refused(() => preparation.ValidateAccount(AccountId));
            Check(File.ReadAllBytes(path).SequenceEqual(before), "Rejected configuration must retain original bytes");
        }
        File.WriteAllText(path, "{\"machineID\":\"unchanged\"}");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { preparation.PrepareExistingAccounts(cancelled.Token); Check(false, "Cancelled preparation must stop"); }
        // 调用方取消是预期结果，文件仍应保持尚未加入 userID 的原文。
        catch (OperationCanceledException) { }
        Check(File.ReadAllText(path) == "{\"machineID\":\"unchanged\"}");
        preparation.PrepareAccount(AccountId);
        Check(File.ReadAllText(path) == "{\"machineID\":\"unchanged\",\"userID\":\"" + preparation.DeviceId + "\"}");
    }

    /// <summary>已有过期假凭据但派生身份不同或损坏时，在启动任何续期子进程前拒绝；普通读取绝不代为修复。</summary>
    public static async Task ValidateBeforeRefresh()
    {
        using var claude = new FakeClaudeLauncher("success");
        await using var web = await WebFixture.Create(overrides: claude.Register);
        var directory = DirectoryFor(web); WindowsSecrets.ProtectDirectory(directory);
        var path = Path.Combine(directory, ".claude.json");
        var wrong = "{\"userID\":\"" + new string('b', 64) + "\",\"machineID\":\"keep\"}";
        File.WriteAllText(path, wrong); FakeClaude.Write(directory, "fixture-expired-never-refreshed", -1);
        var credentialPath = Path.Combine(directory, OfficialCredential.FileName); var credential = File.ReadAllBytes(credentialPath);
        var accounts = web.App.Services.GetRequiredService<IOfficialAccountLogin>();
        Refused(() => accounts.ReadAccessToken(AccountId));
        Check(File.ReadAllText(path) == wrong && !File.Exists(Path.Combine(directory, "fake-calls.log")), "Validation must reject before starting the refresh CLI");
        Check(File.ReadAllBytes(credentialPath).SequenceEqual(credential));
        web.App.Services.GetRequiredService<IClaudeConfigurationPreparation>().PrepareAccount(AccountId);
        Check(accounts.ReadAccessToken(AccountId) == FakeClaude.RefreshedToken, "Explicit preparation makes a subsequent fake refresh possible");
        using var prepared = JsonDocument.Parse(File.ReadAllBytes(path));
        Check(prepared.RootElement.GetProperty("userID").GetString() == web.Settings.Read().Value.DeviceId);
        Check(prepared.RootElement.GetProperty("machineID").GetString() == "keep");
    }

    /// <summary>
    /// 账户编号取自私有配置 oauthAccount.accountUuid：本代身份下命中缓存不再读文件，重新准备后失效读到新值；
    /// 缺失、类型不对或不是标准 UUID 时本地拒绝且不缓存失败，文件补好后下一次即可读到。
    /// </summary>
    public static async Task AccountUuidFromPrivateConfiguration()
    {
        await using var web = await WebFixture.Create();
        var preparation = web.App.Services.GetRequiredService<IClaudeConfigurationPreparation>();
        var directory = DirectoryFor(web); WindowsSecrets.ProtectDirectory(directory);
        var path = Path.Combine(directory, ".claude.json");
        const string first = "11111111-2222-4333-8444-555555555555", second = "66666666-7777-4888-9999-aaaaaaaaaaaa";
        string With(string account) => "{\"userID\":\"" + preparation.DeviceId + "\",\"oauthAccount\":{\"emailAddress\":\"x@example.com\",\"accountUuid\":" + account + "}}";
        File.WriteAllText(path, With("\"" + first + "\"")); preparation.PrepareAccount(AccountId); preparation.ValidateAccount(AccountId);
        Check(preparation.AccountUuid(AccountId) == first, "The account UUID comes from oauthAccount.accountUuid");
        File.WriteAllText(path, With("\"" + second + "\""));
        Check(preparation.AccountUuid(AccountId) == first, "Within one identity generation the cached value is used without reading the file again");
        preparation.PrepareExistingAccounts();
        Check(preparation.AccountUuid(AccountId) == second, "Preparing again invalidates the cached account UUID");
        foreach (var invalid in new[] { "{\"userID\":\"" + preparation.DeviceId + "\"}", With("null"), With("12"), With("\"not-a-uuid\""), With("\"{" + first + "}\"") })
        {
            File.WriteAllText(path, invalid); preparation.PrepareAccount(AccountId);
            Refused(() => preparation.AccountUuid(AccountId));
        }
        File.WriteAllText(path, With("\"" + first + "\""));
        Check(preparation.AccountUuid(AccountId) == first, "A refused read is not cached; the next read after the file is fixed succeeds");
    }

    /// <summary>
    /// 照原生登录保存账户资料：没有 oauthAccount 时补在根对象末尾、其余字节不动；同一账户再存不写文件；换账户整段替换并同步缓存；
    /// 编号不是标准 UUID 时拒绝且文件不变。
    /// </summary>
    public static async Task RecordAccountFollowsOfficialProfile()
    {
        await using var web = await WebFixture.Create();
        var preparation = web.App.Services.GetRequiredService<IClaudeConfigurationPreparation>();
        var directory = DirectoryFor(web); WindowsSecrets.ProtectDirectory(directory);
        var path = Path.Combine(directory, ".claude.json");
        const string first = "11111111-2222-4333-8444-555555555555", second = "66666666-7777-4888-9999-aaaaaaaaaaaa";
        var head = "{\r\n  \"machineID\": \"keep\", \"userID\" : \"" + new string('a', 64) + "\", \"unknown\": {\"escaped\":\"\\u4e2d\"}\r\n";
        byte[] original = [0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes(head + "}\r\n")]; File.WriteAllBytes(path, original);
        preparation.PrepareAccount(AccountId);
        var prepared = File.ReadAllBytes(path);
        Refused(() => preparation.AccountUuid(AccountId));
        preparation.RecordAccount(AccountId, new(first, "x@example.com", "99999999-8888-4777-a666-555555555555"));
        var recorded = File.ReadAllBytes(path);
        var close = Array.LastIndexOf(prepared, (byte)'}');
        Check(recorded.AsSpan(0, close).SequenceEqual(prepared.AsSpan(0, close)) && recorded.AsSpan(recorded.Length - 3).SequenceEqual(prepared.AsSpan(prepared.Length - 3)),
            "Adding oauthAccount keeps every existing byte, BOM and the closing bytes");
        using (var json = JsonDocument.Parse(recorded.AsMemory(3)))
        {
            var account = json.RootElement.GetProperty("oauthAccount");
            Check(account.GetProperty("accountUuid").GetString() == first && account.GetProperty("emailAddress").GetString() == "x@example.com"
                && json.RootElement.GetProperty("machineID").GetString() == "keep", "oauthAccount holds the official profile fields like a native login");
        }
        Check(preparation.AccountUuid(AccountId) == first, "The recorded UUID is used immediately");
        var modified = File.GetLastWriteTimeUtc(path);
        preparation.RecordAccount(AccountId, new(first, "x@example.com", null));
        Check(File.GetLastWriteTimeUtc(path) == modified && File.ReadAllBytes(path).SequenceEqual(recorded), "The same account is not written again");
        preparation.RecordAccount(AccountId, new(second, "y@example.com", null));
        using (var json = JsonDocument.Parse(File.ReadAllBytes(path).AsMemory(3)))
        {
            var account = json.RootElement.GetProperty("oauthAccount");
            Check(account.GetProperty("accountUuid").GetString() == second && account.GetProperty("emailAddress").GetString() == "y@example.com"
                && !account.TryGetProperty("organizationUuid", out _), "A different account replaces the whole oauthAccount");
        }
        Check(preparation.AccountUuid(AccountId) == second, "A changed account updates the cached UUID at once");
        var before = File.ReadAllBytes(path);
        Refused(() => preparation.RecordAccount(AccountId, new("not-a-uuid", null, null)));
        Check(File.ReadAllBytes(path).SequenceEqual(before) && preparation.AccountUuid(AccountId) == second, "An invalid UUID is refused without touching the file or cache");
    }

    /// <summary>非法账户主键与工程外目录均拒绝；允许的独立诊断目录在启动前得到本站编号。</summary>
    public static async Task RestrictTransientPaths()
    {
        await using var web = await WebFixture.Create();
        var preparation = web.App.Services.GetRequiredService<IClaudeConfigurationPreparation>();
        Refused(() => preparation.PrepareAccount("../personal"));
        Refused(() => preparation.PrepareTransient(Path.Combine(web.Home.Path, "personal", "config")));
        Check(!Directory.Exists(Path.Combine(web.Home.Path, "personal")), "Rejected paths must not be created");
        var directory = Path.Combine(Path.GetTempPath(), "HouseholdRelay-diagnostic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var config = Path.Combine(directory, "config"); preparation.PrepareTransient(config);
            using var json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(config, ".claude.json")));
            Check(json.RootElement.GetProperty("userID").GetString() == preparation.DeviceId);
            Check(!File.Exists(Path.Combine(config, OfficialCredential.FileName)), "Temporary diagnostics never create login credentials");
        }
        // 只清理本用例在系统临时根下创建且再次核对过范围的诊断目录。
        finally
        {
            Check(Path.GetDirectoryName(Path.GetFullPath(directory)) == Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())));
            // 校验完成，只删除刚创建的单个诊断目录。
            Directory.Delete(directory, true);
        }
    }

    /// <summary>账户目录被替换为目录联接时，准备与校验均在读写外部配置前拒绝，外部文件和 ACL 不变。</summary>
    public static async Task RefuseDirectoryJunction()
    {
        await using var web = await WebFixture.Create();
        var preparation = web.App.Services.GetRequiredService<IClaudeConfigurationPreparation>();
        var target = Path.Combine(web.Home.Path, "outside-account-root"); Directory.CreateDirectory(target);
        var path = Path.Combine(target, ".claude.json"); File.WriteAllText(path, "{\"userID\":\"untouched\"}");
        var before = File.ReadAllBytes(path); var acl = new DirectoryInfo(target).GetAccessControl().GetSecurityDescriptorBinaryForm();
        var link = DirectoryFor(web); Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot create fixture junction");
        await process.WaitForExitAsync(); Check(process.ExitCode == 0, "Fixture junction must be created");
        try
        {
            Refused(() => preparation.PrepareAccount(AccountId, true)); Refused(() => preparation.ValidateAccount(AccountId));
            Refused(() => preparation.PrepareExistingAccounts());
            Check(File.ReadAllBytes(path).SequenceEqual(before), "Linked destination must remain untouched");
            Check(new DirectoryInfo(target).GetAccessControl().GetSecurityDescriptorBinaryForm().SequenceEqual(acl), "Linked destination ACL must remain untouched");
        }
        // 只删除本用例创建且确认仍为重解析点的链接本身，不递归触碰它的目标。
        finally
        {
            Check((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
            // 无递归删除仅移除已验证的目录联接。
            Directory.Delete(link);
        }
    }

    /// <summary>测试账户目录固定在本次隔离实例的 .state 中。</summary>
    private static string DirectoryFor(WebFixture web) => Path.Combine(web.Home.Path, ".state", "official-accounts", AccountId);

    /// <summary>执行应被本地配置边界拒绝的操作，不接受无声成功。</summary>
    private static void Refused(Action action)
    {
        var refused = false;
        try { action(); }
        // 固定配置拒绝是用例目标；不把其他异常或外部调用失败当成预期结果。
        catch (InvalidOperationException) { refused = true; }
        Check(refused, "Unsafe or inconsistent configuration must be rejected locally");
    }
}
