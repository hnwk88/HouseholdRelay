using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Application.Interfaces.ClaudeCode;
using Application.Interfaces.Configuration;
using Application.Interfaces.Relay;
using Application.Interfaces.Upstream;
using Core.Configuration;
using Core.Siyu;
using Core.Upstream;

namespace Infrastructure.ExternalSources.ClaudeCode;

/// <summary>
/// 照 MyCoAI <c>AuthCliRunner</c> 委托本机 Claude Code 登录官方账户：每个账户一个独立配置目录 <c>.state\official-accounts\上游主键</c>，
/// 子进程环境由 <see cref="IsolatedClaudeEnvironment"/> 隔离、代理按全局设置；令牌刷新交给 CLI，中转只读最新的凭据文件。
/// </summary>
internal sealed class OfficialAccountLogin : IOfficialAccountLogin
{
    private readonly IClientLauncher _launcher;
    private readonly IUpstreamProxy _proxy;
    private readonly IRelayInstance _instance;
    private readonly TimeProvider _clock;
    private readonly OfficialLoginOptions _options;
    private readonly IClaudeConfigurationPreparation _configuration;
    private readonly ISiteIdentityService _siteIdentity;
    private readonly IOwnedClaudeProcesses _ownedProcesses;
    private readonly IProtocolDiagnostics _diagnostics;

    /// <summary>九个依赖全部必填，构造时当场判空：协议诊断不再可省，漏登记在启动时就报，不会静默少记续期诊断。</summary>
    public OfficialAccountLogin(IClientLauncher launcher, IUpstreamProxy proxy, IRelayInstance instance, TimeProvider clock, OfficialLoginOptions options, IClaudeConfigurationPreparation configuration, ISiteIdentityService siteIdentity, IOwnedClaudeProcesses ownedProcesses, IProtocolDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(launcher); ArgumentNullException.ThrowIfNull(proxy); ArgumentNullException.ThrowIfNull(instance); ArgumentNullException.ThrowIfNull(clock); ArgumentNullException.ThrowIfNull(options); ArgumentNullException.ThrowIfNull(configuration); ArgumentNullException.ThrowIfNull(siteIdentity); ArgumentNullException.ThrowIfNull(ownedProcesses); ArgumentNullException.ThrowIfNull(diagnostics);
        _launcher = launcher; _proxy = proxy; _instance = instance; _clock = clock; _options = options; _configuration = configuration; _siteIdentity = siteIdentity; _ownedProcesses = ownedProcesses; _diagnostics = diagnostics;
    }

    /// <summary>令牌离到期不足这么久就让 CLI 刷新。</summary>
    private static readonly TimeSpan _refreshMargin = TimeSpan.FromMinutes(5);
    /// <summary>令牌已过期时，转发最多等刷新这么久。</summary>
    private static readonly TimeSpan _refreshWait = TimeSpan.FromSeconds(30);
    /// <summary>同一账户两次刷新之间至少隔这么久，刷新失败时不至于每个请求都起一个 CLI。</summary>
    private static readonly TimeSpan _refreshInterval = TimeSpan.FromMinutes(1);
    /// <summary>删除账户时最多等被取消的刷新结束这么久，再删目录。</summary>
    private static readonly TimeSpan _removeWait = TimeSpan.FromSeconds(10);
    /// <summary>登录、刷新与抓取统一身份按官方接口地址解析代理。</summary>
    internal static readonly Uri OfficialTarget = new("https://" + UpstreamDetection.OfficialHost);
    // 取消源只取消不释放：没有计时器也不取等待句柄，不占非托管资源；释放了反而会让删除时的取消撞上已释放的对象。
    private readonly Dictionary<string, (Task Task, DateTimeOffset StartedAt, CancellationTokenSource Stop)> _refreshes = new(StringComparer.Ordinal);
    /// <summary>已删除的账户主键；与 <see cref="_refreshes"/> 同一把锁，删除后不再起刷新，刷新结束时发现已删除就再删一次目录。</summary>
    private readonly HashSet<string> _removed = new(StringComparer.Ordinal);
    /// <summary>已导出搬走的账户主键；与 <see cref="_refreshes"/> 同一把锁，搬走后不再起刷新，账户目录留着等管理员删除。</summary>
    private readonly HashSet<string> _moved = new(StringComparer.Ordinal);
    /// <summary>正在换上重新登录得到的凭据的账户主键；与 <see cref="_refreshes"/> 同一把锁，换的这一小会儿不起刷新，免得 CLI 与换文件同时改写凭据。</summary>
    private readonly HashSet<string> _adopting = new(StringComparer.Ordinal);
    /// <summary>搬走时最多等正在跑的刷新结束这么久：刷新本身限时 60 秒，多留一点余量。</summary>
    private static readonly TimeSpan _moveWait = TimeSpan.FromSeconds(70);
    /// <summary>每个账户最近见到的访问令牌哈希与凭据代次；只在内存，服务重启从 1 重新开始（会话也随重启失效）。</summary>
    private readonly Dictionary<string, (byte[] Hash, long Generation)> _generations = new(StringComparer.Ordinal);

    /// <summary>
    /// 定位 claude.exe、按全局设置解析代理（启用却取不到时抛出 <see cref="UpstreamProxyUnavailableException"/>），建好受保护的配置目录后运行 <c>claude auth login</c> 等它结束；
    /// 成功且目录里有凭据时用 <c>auth status --json</c> 取邮箱。任何失败都删掉这个目录再抛出读给管理员的原因。
    /// </summary>
    public async Task<string?> LoginAsync(string accountId, CancellationToken cancellation)
    {
        using var operation = _siteIdentity.Enter();
        string executable;
        try { executable = _launcher.FindClaude(); }
        // 定位失败只说明本机没装，换成登录场景的原因与下一步。
        catch (InvalidOperationException) { throw new InvalidOperationException("本机没有找到 Claude Code，无法登录官方账户。请先安装 Claude Code，再按「登录官方账户」。"); }
        var route = _proxy.Resolve(OfficialTarget);
        var directory = Directory(accountId);
        _configuration.PrepareAccount(accountId, true);
        try
        {
            var start = Start(executable, directory, route, "auth", "login");
            if (_options.ShowWindow) { start.CreateNoWindow = false; start.WindowStyle = ProcessWindowStyle.Minimized; }
            else start.CreateNoWindow = true;
            // 调用方取消（浏览器断开）与登录限时合成一个令牌，任一触发都结束登录进程；两者靠调用方令牌是否已取消来区分读法。
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation); limit.CancelAfter(_options.LoginTimeout);
            int exit;
            using var owned = _ownedProcesses.Starting(); var registered = false;
            using (var process = StartOwned(start, owned) ?? throw new InvalidOperationException("无法启动 Claude Code 登录，请再按一次「登录官方账户」。"))
            {
                try
                {
                    owned.Register(process.Id, process.StartTime.ToUniversalTime()); registered = true;
                    await process.WaitForExitAsync(limit.Token); exit = process.ExitCode;
                }
                // 只有限时到了才换成超时说明；浏览器断开等调用方取消原样抛出。
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                { throw new InvalidOperationException($"等了 {_options.LoginTimeout.TotalMinutes:0} 分钟还没有登录完成，已停止。需要时再按「登录官方账户」重试。"); }
                // 超时、取消或等待出错时结束登录进程树，不留后台进程。
                finally
                {
                    if (!process.HasExited) process.Kill(true);
                    await process.WaitForExitAsync(CancellationToken.None);
                    if (registered) owned.Complete();
                }
            }
            if (exit != 0) throw new InvalidOperationException($"登录没有完成（Claude Code 登录以退出码 {exit} 结束），可能是在浏览器里取消了。需要时再按「登录官方账户」重试。");
            if (Read(directory) is null)
                throw new InvalidOperationException("登录命令结束了，但没有拿到官方账户凭据。请再按一次「登录官方账户」；仍然不行时可以改用下面的「贴账户访问令牌」。");
            return await EmailAsync(executable, directory, route, cancellation);
        }
        // 登录没成功就不留半截目录；异常原样交给调用方读出。
        catch { RemoveDirectory(directory); throw; }
    }

    /// <summary>
    /// 先在内存里校验登录文件（不超过 <see cref="Application.Contracts.Uploads.UploadLimits.OfficialCredentialBytes"/>、是 JSON、有访问令牌和刷新令牌），再定位 claude.exe、解析代理，
    /// 合格才建受保护的账户目录、把文件原样写进去，用 <c>auth status --json</c> 取邮箱返回。任何失败都删掉目录；读给管理员的原因不含文件内容。
    /// </summary>
    public async Task<string?> ImportAsync(string accountId, byte[] content, CancellationToken cancellation)
    {
        using var operation = _siteIdentity.Enter();
        ValidateImport(content);
        string executable;
        try { executable = _launcher.FindClaude(); }
        // 定位失败只说明本机没装；导入的令牌到期要靠它续期，所以照登录的口径拒绝。
        catch (InvalidOperationException) { throw new InvalidOperationException("本机没有找到 Claude Code，无法导入登录文件（令牌到期要靠它自动续期）。请先安装 Claude Code，再导入。"); }
        var route = _proxy.Resolve(OfficialTarget);
        var directory = Directory(accountId);
        _configuration.PrepareAccount(accountId, true);
        try
        {
            File.WriteAllBytes(Path.Combine(directory, OfficialCredential.FileName), content);
            return await EmailAsync(executable, directory, route, cancellation);
        }
        // 写文件或取邮箱途中被取消等失败：不留半截目录，异常原样交给调用方读出。
        catch { RemoveDirectory(directory); throw; }
    }

    /// <summary>账户目录里有可用的凭据文件；只读文件，不刷新、不等待，供打开页面时判断「已配置」。</summary>
    public bool HasCredential(string accountId) => Read(Directory(accountId)) is not null;

    /// <summary>读最新凭据；离到期不足 <see cref="_refreshMargin"/> 时在后台让 CLI 刷新，已过期时最多等 <see cref="_refreshWait"/> 后重读，刷新不成仍返回旧令牌，由上游的拒绝触发重新登录提示。</summary>
    public string? ReadAccessToken(string accountId)
    {
        // 已删除或搬走的账户不再读文件，也不能因查询状态而重新启动续期。
        lock (_refreshes) if (_removed.Contains(accountId) || _moved.Contains(accountId)) return null;
        using var operation = _siteIdentity.Enter();
        _configuration.ValidateAccount(accountId);
        var directory = Directory(accountId);
        var credential = Read(directory);
        if (credential?.ExpiresAt is not { } expires || expires - _clock.GetUtcNow() > _refreshMargin) return credential?.AccessToken;
        var refresh = RefreshAsync(accountId, directory);
        if (expires > _clock.GetUtcNow()) return credential.AccessToken;
        refresh.Wait(_refreshWait);
        return Read(directory)?.AccessToken ?? credential.AccessToken;
    }

    /// <summary>
    /// 思域发送用的有效凭据：离到期远就用当前令牌；快到期时起后台续期（同 <see cref="RefreshAsync"/> 的单飞）并用仍有效的当前令牌；
    /// 已过期就等那次续期（最多 <see cref="_refreshWait"/>，按调用方令牌可单独放弃等待）再重读，仍过期一律拒发。等待期间不占站点维护许可。
    /// </summary>
    public async Task<OfficialCredentialLease> AcquireCredentialAsync(string accountId, CancellationToken cancellation)
    {
        ThrowIfGone(accountId);
        var directory = Directory(accountId);
        var credential = Guarded(() => { _configuration.ValidateAccount(accountId); return Read(directory); });
        if (credential is null) throw Reauthenticate();
        if (credential.ExpiresAt is not { } expires)
            throw new SiyuRefusal(503, "official-expiry-unknown", "这个官方账户的凭据没有写到期时间，思域不能确认它仍有效。请在中转机管理页重新登录该账户，再启动思域。");
        if (expires - _clock.GetUtcNow() > _refreshMargin) return Lease(accountId, credential.AccessToken, expires);
        var refresh = Guarded(() => RefreshAsync(accountId, directory));
        if (expires > _clock.GetUtcNow()) return Lease(accountId, credential.AccessToken, expires);
        try { await refresh.WaitAsync(_refreshWait, _clock, cancellation); }
        // 等到上限还没续完：不再等，按重读结果判断；调用方自己的取消不在这里接，原样抛出。
        catch (TimeoutException) { }
        var renewed = Guarded(() => Read(directory));
        if (renewed?.ExpiresAt is { } renewedExpires && renewedExpires > _clock.GetUtcNow()) return Lease(accountId, renewed.AccessToken, renewedExpires);
        throw Reauthenticate();
    }

    /// <summary>思域启动资料：私有配置的 oauthAccount 原字节加凭据文件的 claudeAiOauth，交给字段表核对；不触发续期。</summary>
    public SiyuLaunchProfile ReadLaunchProfile(string accountId)
    {
        ThrowIfGone(accountId);
        var directory = Directory(accountId);
        return Guarded(() =>
        {
            var account = _configuration.ReadOAuthAccount(accountId);
            var credential = ReadOAuthSection(directory) ?? throw Reauthenticate();
            return SiyuLaunchProfileContract.Read(account, credential);
        });
    }

    /// <summary>已删除或搬走的账户不再给思域用。</summary>
    private void ThrowIfGone(string accountId)
    {
        // 与删除、搬走共用这把锁，读到的是最新状态。
        lock (_refreshes)
            if (_removed.Contains(accountId) || _moved.Contains(accountId))
                throw new SiyuRefusal(503, "official-account-gone", "这个官方账户已经删除或搬走，思域不能再用它。请在 MyCoAI 改选别的账户。");
    }

    /// <summary>在站点维护许可内做一次只读操作；维护中、私有配置不可用都换成思域的本地拒绝，许可在返回前就归还，不跨等待。</summary>
    private T Guarded<T>(Func<T> read)
    {
        try
        {
            using var operation = _siteIdentity.Enter();
            return read();
        }
        // 维护中：官方收不到请求，按维护读句拒绝，不进暂停冷却。
        catch (SiteIdentityUnavailableException)
        { throw new SiyuRefusal(503, "site-maintenance", "思域因设备维护已停止。维护完成后请重新启动，原会话不会自动换号。"); }
        // 私有配置的拒绝读句是固定说明（不含文件内容），原样带给使用者。
        catch (ClaudeConfigurationUnavailableException error)
        { throw new SiyuRefusal(503, "account-configuration", error.Message); }
    }

    /// <summary>官方凭据过期且续期失败、或凭据不在。</summary>
    private static SiyuRefusal Reauthenticate() =>
        new(503, "official-reauthentication-required", "此官方账户需要重新登录。请在中转机管理页重新登录该账户，再启动思域。");

    /// <summary>按令牌内容给出凭据代次：与这个账户上一次见到的令牌相同就沿用，变了就加一。比较材料只留哈希、只在内存。</summary>
    private OfficialCredentialLease Lease(string accountId, string accessToken, DateTimeOffset expiresAt)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(accessToken));
        long generation;
        // 并发取凭据时比较与登记新代次必须原子，否则同一新令牌可能被记成两个代次。
        lock (_generations)
        {
            if (_generations.TryGetValue(accountId, out var last) && CryptographicOperations.FixedTimeEquals(last.Hash, hash)) generation = last.Generation;
            else { generation = last.Generation + 1; _generations[accountId] = (hash, generation); }
        }
        return new OfficialCredentialLease(accountId, accessToken, expiresAt, generation);
    }

    /// <summary>读凭据文件里 claudeAiOauth 对象的原始字节；文件不在、不是对象或读不全时返回 null。读出的整文件字节用完清零。</summary>
    private static byte[]? ReadOAuthSection(string directory)
    {
        var content = ReadFile(directory);
        if (content is null) return null;
        try
        {
            using var document = JsonDocument.Parse(content);
            return document.RootElement.TryGetProperty("claudeAiOauth", out var oauth) && oauth.ValueKind == JsonValueKind.Object
                ? Encoding.UTF8.GetBytes(oauth.GetRawText()) : null;
        }
        // 文件里有访问令牌和刷新令牌：无论成败都清零读到的整份。
        finally { CryptographicOperations.ZeroMemory(content); }
    }

    /// <summary>读官方账户当前的 oauthAccount.accountUuid（发往官方时对齐账户段用）；已删除或搬走的账户返回 null，缺失或格式不对时抛出由上游读取处按本地身份阻断处理。不读令牌、不触发续期。</summary>
    public string? ReadAccountUuid(string accountId)
    {
        // 与删除、搬走共用这把锁，读到的是最新状态：已删除或搬走的账户不再读私有配置，也不因此触发续期。
        lock (_refreshes) if (_removed.Contains(accountId) || _moved.Contains(accountId)) return null;
        return _configuration.AccountUuid(accountId);
    }

    /// <summary>把官方返回的账户资料存进账户私有配置；已删除或搬走的账户不写（搬走后本机不再用它）。</summary>
    public void RecordAccount(string accountId, Core.Upstream.OfficialAccountProfile profile)
    {
        // 与删除、搬走共用这把锁判断最新状态。
        lock (_refreshes) if (_removed.Contains(accountId) || _moved.Contains(accountId)) throw new InvalidOperationException("这个官方账户已经删除或搬走，本机不再保存它的账户资料。");
        _configuration.RecordAccount(accountId, profile);
    }

    /// <summary>
    /// 上游被删除时删掉它的账户目录：先记为已删除并取消正在跑的刷新，等它结束进程树（最多 <see cref="_removeWait"/>）再删，免得 CLI 占着文件或把目录重新写出；
    /// 主键不合格式时拒绝，删不掉时放弃。
    /// </summary>
    public void Remove(string accountId)
    {
        var directory = Directory(accountId);
        (Task Task, DateTimeOffset StartedAt, CancellationTokenSource Stop)? running = null;
        // 记已删除与取出正在跑的刷新在同一把锁里：否则刷新可能在两步之间登记，删除后又起一个 CLI 写出目录。
        lock (_refreshes)
        {
            _removed.Add(accountId);
            if (_refreshes.Remove(accountId, out var last) && !last.Task.IsCompleted) running = last;
        }
        // 取消放在锁外：取消回调会同步结束进程树，不该占着刷新登记表的锁。
        if (running is { } refresh) { refresh.Stop.Cancel(); Task.WhenAny(refresh.Task, Task.Delay(_removeWait)).Wait(); }
        // 刚被结束的进程树可能还占着这个工作目录一小会儿：删不掉就稍等重试，最多约 5 秒，之后放弃。
        for (var attempt = 0; attempt < 25 && RemoveDirectory(directory); attempt++) Thread.Sleep(200);
    }

    /// <summary>
    /// 重新登录换凭据：把 <paramref name="fromAccountId"/>（刚在临时目录里登录得到的）的凭据文件换进 <paramref name="accountId"/> 的账户目录，账户目录里的私有配置（设备编号、账户资料）原样不动。
    /// 换之前不再起刷新，并等正在跑的刷新自己结束（最多 <see cref="_moveWait"/>，不取消，免得 CLI 死在改写凭据文件的半途）；先写同目录的临时文件再整份替换，读的一方不会读到半截。
    /// 已删除或搬走的账户、等不完刷新时拒绝，原凭据不动。临时目录由调用方删除。
    /// </summary>
    public async Task AdoptAsync(string accountId, string fromAccountId)
    {
        var directory = Directory(accountId); var source = Path.Combine(Directory(fromAccountId), OfficialCredential.FileName);
        Task? running;
        // 判断已删除或搬走、登记「正在换」与取出正在跑的刷新在同一把锁里：否则刷新可能在两步之间登记，与换文件同时改写凭据。
        lock (_refreshes)
        {
            if (_removed.Contains(accountId) || _moved.Contains(accountId)) throw new InvalidOperationException("这个官方账户已经删除或搬走，本机不再用它，不能重新登录。");
            if (!_adopting.Add(accountId)) throw new InvalidOperationException("这个官方账户正在换上新的登录，请等这一次结束再看结果。");
            running = _refreshes.TryGetValue(accountId, out var last) && !last.Task.IsCompleted ? last.Task : null;
        }
        try
        {
            // 有刷新正在跑：等它自己结束（不取消，CLI 可能正在改写凭据文件），等不到就不换，免得两边同时写同一个文件。
            if (running is not null && await Task.WhenAny(running, Task.Delay(_moveWait)) != running)
                throw new InvalidOperationException("这个官方账户正在续期，等了一会儿还没结束，没有换上新的登录。请稍后再按「重新登录」。");
            var temporary = Path.Combine(directory, OfficialCredential.FileName + ".relogin");
            File.Copy(source, temporary, true);
            File.Move(temporary, Path.Combine(directory, OfficialCredential.FileName), true);
            // 换上的是新凭据：清掉上一次刷新的登记，快到期时不必再等刷新间隔。
            lock (_refreshes) _refreshes.Remove(accountId);
        }
        // 无论换成没换成都撤掉「正在换」的登记，这个账户照常续期；换失败时原凭据文件没被动过。
        finally { lock (_refreshes) _adopting.Remove(accountId); }
    }

    /// <summary>
    /// 导出即搬走：先记为已搬走（此后不再起刷新），等这些账户正在跑的刷新自己结束（最多 <see cref="_moveWait"/>，不取消，免得 CLI 死在改写凭据文件的半途），
    /// 再按顺序读出各自的凭据文件原文交给 <paramref name="commit"/>（加密打包并在库里记为已导出）。读不到凭据、等不完刷新或 <paramref name="commit"/> 抛出时，
    /// 本次新记的账户恢复续期，异常原样抛出；读出的文件字节无论成败都清零。账户目录不删。
    /// </summary>
    public async Task<T> ExportAsync<T>(IReadOnlyList<string> accountIds, Func<IReadOnlyList<byte[]>, Task<T>> commit)
    {
        var directories = accountIds.Select(Directory).ToArray();
        var added = new List<string>(); var running = new List<Task>();
        // 记已搬走与取出正在跑的刷新在同一把锁里：否则刷新可能在两步之间登记，搬走之后又换一次令牌，导出的那份就作废了。
        lock (_refreshes)
            foreach (var id in accountIds)
            {
                if (_moved.Add(id)) added.Add(id);
                if (_refreshes.TryGetValue(id, out var last) && !last.Task.IsCompleted) running.Add(last.Task);
            }
        var files = new List<byte[]>();
        try
        {
            // 并行等所有正在跑的刷新：它们写完凭据文件再读，导出的才是最新令牌；等不完就不导出，免得拿到马上作废的旧令牌。
            if (!Task.WhenAll(running).Wait(_moveWait))
                throw new InvalidOperationException("要导出的官方账户正在续期，还没结束。请过一分钟再按「导出加密文件」。");
            foreach (var directory in directories)
                files.Add(ReadFile(directory) ?? throw new InvalidOperationException("要导出的官方账户里有一家的登录凭据已经不在了。请不勾选它再导出，或者删除它后重新登录。"));
            return await commit(files);
        }
        // 没导出成：本次新记的账户照常续期，之前就已搬走的保持原样。
        catch { lock (_refreshes) foreach (var id in added) _moved.Remove(id); throw; }
        // 登录文件里有访问令牌和刷新令牌：交出去加密以后无论成败都清零。
        finally { foreach (var file in files) CryptographicOperations.ZeroMemory(file); }
    }

    /// <summary>同一账户同时只跑一次刷新，且两次之间至少隔 <see cref="_refreshInterval"/>；否则返回上一次的任务。已删除或已搬走的账户不再刷新。</summary>
    private Task RefreshAsync(string accountId, string directory)
    {
        // 并发请求同时发现令牌快到期：查上一次刷新与登记新刷新必须在同一把锁里，否则会各起一个 CLI 同时改写凭据文件。
        lock (_refreshes)
        {
            if (_removed.Contains(accountId) || _moved.Contains(accountId) || _adopting.Contains(accountId)) return Task.CompletedTask;
            if (_refreshes.TryGetValue(accountId, out var last) && (!last.Task.IsCompleted || _clock.GetUtcNow() - last.StartedAt < _refreshInterval)) return last.Task;
            var operation = _siteIdentity.Enter();
            var stop = new CancellationTokenSource();
            // 后台任务独立占一个许可，调用方先返回也不会让维护漏掉仍在写凭据的 CLI。
            var task = Task.Run(async () => { using (operation) await RunRefreshAsync(accountId, directory, stop.Token); });
            _refreshes[accountId] = (task, _clock.GetUtcNow(), stop);
            return task;
        }
    }

    /// <summary>
    /// 在账户目录里跑一次 <c>claude -p /usage</c>（MyCoAI 用的本地免费命令，不花模型额度）：CLI 发现令牌快到期会自己用刷新令牌换新并写回凭据文件。
    /// 失败或被删除取消只说明这次没刷新成，不抛出，转发继续用旧令牌；结束时所属上游已删除就再删一次目录。
    /// </summary>
    private async Task RunRefreshAsync(string accountId, string directory, CancellationToken stop)
    {
        try
        {
            // 启动续期程序之前重读一次私有配置，不用本代缓存。
            _configuration.ValidateAccount(accountId, reread: true);
            var start = Start(_launcher.FindClaude(), directory, _proxy.Resolve(OfficialTarget), "-p", "/usage", "--output-format", "json", "--no-session-persistence");
            var (exit, _) = await RunHiddenAsync(start, TimeSpan.FromSeconds(60), stop);
            _diagnostics.Record(Guid.NewGuid().ToString("N"), "cli_completed", "cli", _siteIdentity.Current.Version, _siteIdentity.Current.DeviceId,
                status: exit, error: exit == 0 ? null : "renewal_failed");
        }
        // 没装 Claude Code、代理取不到、进程起不来、超时或因删除被取消：这次不刷新，令牌真失效时上游拒绝会提示重新登录。
        catch (Exception error) when (error is InvalidOperationException or Win32Exception or IOException or OperationCanceledException)
        {
            _diagnostics.Record(Guid.NewGuid().ToString("N"), "cli_failed", "cli", _siteIdentity.Current.Version, _siteIdentity.Current.DeviceId, error: "renewal_failed");
        }
        bool gone;
        // 已删除集合与删除方共用这把锁，读到的是删除之后的最新状态。
        lock (_refreshes) gone = _removed.Contains(accountId);
        // 刷新途中上游被删：CLI 可能在删除之后又写出了目录，这里补删，不留残目录。
        if (gone) RemoveDirectory(directory);
    }

    /// <summary>导入前的内存校验：超过上限、不是 JSON、没有访问令牌、没有刷新令牌各读出原因与下一步，不复述文件内容。</summary>
    private static void ValidateImport(byte[] content)
    {
        const string Where = "另一台电脑上的 %USERPROFILE%\\.claude\\.credentials.json";
        if (content.Length > Application.Contracts.Uploads.UploadLimits.OfficialCredentialBytes) throw new InvalidOperationException($"选的文件超过 64 KB，不是 Claude Code 的登录文件。请选{Where}。");
        OfficialCredential? credential;
        try { credential = OfficialCredential.Parse(content); }
        // 解析错误的消息里可能带文件片段，只读出结论。
        catch (JsonException) { throw new InvalidOperationException($"选的文件不是 JSON，不是 Claude Code 的登录文件。请选{Where}。"); }
        if (credential is null) throw new InvalidOperationException($"选的文件里没有官方账户的访问令牌（claudeAiOauth.accessToken），不是登录成功后的文件。请选{Where}。");
        if (!credential.Refreshable) throw new InvalidOperationException("选的文件里只有访问令牌、没有刷新令牌（refreshToken），导入后到期不能自动续期。请改用下面的「贴账户访问令牌」。");
    }

    /// <summary>跑 <c>auth status --json</c> 取 <c>email</c>；命令失败、超时或输出不是预期 JSON 时返回 null，不影响登录结果。</summary>
    private async Task<string?> EmailAsync(string executable, string directory, ProxyRoute route, CancellationToken cancellation)
    {
        try
        {
            var (exit, output) = await RunHiddenAsync(Start(executable, directory, route, "auth", "status", "--json"), TimeSpan.FromSeconds(30), cancellation);
            if (exit != 0) return null;
            using var document = JsonDocument.Parse(output);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("email", out var email)
                && email.ValueKind == JsonValueKind.String && email.GetString() is { Length: > 0 } value && !value.Any(char.IsControl) ? value.Trim() : null;
        }
        // 只是取不到邮箱：名称退回默认值，登录本身已经成功。
        catch (Exception error) when (error is JsonException or InvalidOperationException or Win32Exception or IOException
            || error is OperationCanceledException && !cancellation.IsCancellationRequested) { return null; }
    }

    /// <summary>组装账户目录里的 Claude Code 子进程：工作目录与配置目录都是账户目录，环境按 <see cref="IsolatedClaudeEnvironment"/> 隔离。</summary>
    // 普通转发对私有配置的核对按身份代次缓存，运行中被外部改坏的 .claude.json 只在 Claude Code 真要读它之前发现。
    // 所以凡是在账户目录里启动 Claude Code 的入口（今天是登录、导入后取邮箱和续期），启动前必须先 PrepareAccount 或 ValidateAccount(reread: true)，新增入口同样如此。
    internal static ProcessStartInfo Start(string executable, string directory, ProxyRoute route, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = directory, UseShellExecute = false };
        IsolatedClaudeEnvironment.Apply(start, directory, route);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    /// <summary>只在操作系统明确拒绝创建进程时撤回本次 starting；启动后的任何异常都保留登记。</summary>
    internal static Process? StartOwned(ProcessStartInfo start, IOwnedClaudeProcess? owned)
    {
        try { return Process.Start(start); }
        // 此 catch 只包创建调用，不能把初始化、登记或等待失败误认为没有启动。
        catch (Win32Exception) { owned?.CancelBeforeStart(); throw; }
    }

    /// <summary>不建窗口运行子进程并读完标准输出，限时 <paramref name="limit"/>；超时或取消时结束进程树并抛出 <see cref="OperationCanceledException"/>。</summary>
    private async Task<(int Exit, string Output)> RunHiddenAsync(ProcessStartInfo start, TimeSpan limit, CancellationToken cancellation)
    {
        start.CreateNoWindow = true; start.RedirectStandardInput = true; start.RedirectStandardOutput = true; start.RedirectStandardError = true;
        start.StandardOutputEncoding = new UTF8Encoding(false); start.StandardErrorEncoding = new UTF8Encoding(false);
        // 调用方取消与本次限时合成一个令牌，读输出和等退出都受它约束，任一触发都在 finally 里结束进程树。
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(limit);
        using var owned = _ownedProcesses.Starting(); var registered = false;
        using var process = StartOwned(start, owned) ?? throw new InvalidOperationException("无法启动 Claude Code。");
        try
        {
            owned.Register(process.Id, process.StartTime.ToUniversalTime()); registered = true;
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token); var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token); await errors;
            return (process.ExitCode, await output);
        }
        // 超时、取消或读写出错时结束进程树，不留后台进程。
        finally
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None);
            if (registered) owned.Complete();
        }
    }

    /// <summary>读并解析凭据文件；CLI 正在改写时可能读到半截或被占用，稍等重读，三次仍不行或文件不存在时返回 null。</summary>
    private static OfficialCredential? Read(string directory)
    {
        var path = Path.Combine(directory, OfficialCredential.FileName);
        for (var attempt = 1; ; attempt++)
        {
            try { return File.Exists(path) ? OfficialCredential.Parse(File.ReadAllBytes(path)) : null; }
            // 半截正文或文件被占用：最多重读三次，之后按没有凭据处理。
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
            { if (attempt == 3) return null; Thread.Sleep(50); }
        }
    }

    /// <summary>读凭据文件原文并确认里面有访问令牌；CLI 正在改写时稍等重读，三次仍不行、文件不存在或没有访问令牌时返回 null。</summary>
    private static byte[]? ReadFile(string directory)
    {
        var path = Path.Combine(directory, OfficialCredential.FileName);
        for (var attempt = 1; ; attempt++)
        {
            byte[]? content = null;
            try
            {
                if (!File.Exists(path)) return null;
                content = File.ReadAllBytes(path);
                if (OfficialCredential.Parse(content) is not null) return content;
                CryptographicOperations.ZeroMemory(content); return null;
            }
            // 半截正文或文件被占用：清掉读到的半截，最多重读三次，之后按没有凭据处理。
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
            { if (content is not null) CryptographicOperations.ZeroMemory(content); if (attempt == 3) return null; Thread.Sleep(50); }
        }
    }

    /// <summary>账户目录的完整路径；主键须是 32 位小写十六进制（上游主键的格式），否则拒绝，防止拼出数据目录以外的路径。</summary>
    private string Directory(string accountId)
    {
        if (accountId.Length != 32 || !accountId.All(char.IsAsciiHexDigitLower)) throw new InvalidOperationException("上游主键无效，请重新加载页面。");
        return Path.Combine(_instance.State, "official-accounts", accountId);
    }

    /// <summary>删除登录失败留下的、或所属上游已删除的账户目录；删不掉时放弃并返回 true（目录还在），残留的只是一个没有凭据或未被任何上游引用的目录。</summary>
    private static bool RemoveDirectory(string directory)
    {
        try { if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, true); return false; }
        // 文件被占用：放弃清理，不掩盖登录失败的真实原因。
        catch (IOException) { return true; }
        // 没有删除权限：同样放弃清理。
        catch (UnauthorizedAccessException) { return true; }
    }
}
