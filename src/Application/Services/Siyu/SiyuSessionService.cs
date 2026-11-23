using System.Security.Cryptography;
using System.Text;
using Application.Contracts.Siyu;
using Application.Interfaces.Configuration;
using Application.Interfaces.Siyu;
using Application.Interfaces.Upstream;
using Application.Interfaces.Usage;
using Core.Configuration;
using Core.Identity;
using Core.Relay;
using Core.Siyu;
using Core.Upstream;
using Core.Usage;

namespace Application.Services.Siyu;

/// <summary>
/// 思域会话的唯一登记处与准入：会话只存内存（服务重启即全部失效），票据只留哈希。
/// 锁只护内存状态，锁内不等网络、不等 CLI、不读文件；关闭时锁内转 Closing，锁外取消，隧道与许可归还完才 Closed。
/// 失效源：个人令牌名单变化（<see cref="ClientRegistry.Subscribe"/>）、站点维护（<see cref="ISiteIdentityService.TransportStopping"/>）、租约到期（定时清扫与每次准入都查）、
/// 资料换代（续租时核对）、官方凭据需要重新登录（准入时）、账户收口（<see cref="CloseAccount"/>，上游删除与导出经 <see cref="OfficialAccountRetirements"/> 触发）。
/// </summary>
public sealed class SiyuSessionService : ISiyuSessionService, ISiyuGate, IDisposable
{
    private readonly ClientRegistry _clients;
    private readonly IUpstreamService _upstreams;
    private readonly IOfficialAccountLogin _officialAccounts;
    private readonly ISiteIdentityService _siteIdentity;
    /// <summary>代理 CA 公钥证书的 PEM，原样交给 MyCoAI。</summary>
    private readonly string _authorityPem;
    /// <summary>代理 CA 证书 DER 的 SHA256（小写十六进制），供 MyCoAI 核对。</summary>
    private readonly string _authoritySha256;
    private readonly SiyuProxyEndpoint _endpoint;
    private readonly McpDiscoveryRegistry _discovery;
    private readonly TimeProvider _clock;
    private readonly ITimer _sweep;
    private readonly object _sync = new();
    private readonly Dictionary<string, SiyuSession> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<(long UserId, long TokenId, string RequestId), SiyuCreateRecord> _creates = [];
    /// <summary>每个官方账户被官方明确拒绝过的最高凭据代次；同代次及更旧的凭据不再发送，新代次照常。</summary>
    private readonly Dictionary<string, long> _rejectedGenerations = new(StringComparer.Ordinal);
    private readonly IMcpDiscoveryProbe _discoveryProbe;
    private readonly IUsageService _usage;
    /// <summary>每个官方账户（上游主键）最近一次客户端真实发出的连接器发现请求头（个人令牌已抹掉），单飞补发现时原样重发。</summary>
    private readonly Dictionary<long, byte[]> _discoveryTemplates = [];
    /// <summary>每个官方账户进行中的补发现；同一账户只跑一份，等的人各自用自己的取消。</summary>
    private readonly Dictionary<long, Task> _discoveryFlights = [];

    /// <summary>依赖全部必填，构造时当场判空；订阅令牌名单变化，挂上定时清扫。</summary>
    public SiyuSessionService(ClientRegistry clients, IUpstreamService upstreams, IOfficialAccountLogin officialAccounts, ISiteIdentityService siteIdentity,
        ISiyuProxyCertificates certificates, SiyuProxyEndpoint endpoint, McpDiscoveryRegistry discovery, IMcpDiscoveryProbe discoveryProbe, IUsageService usage, TimeProvider clock, OfficialAccountRetirements retirements)
    {
        ArgumentNullException.ThrowIfNull(retirements); ArgumentNullException.ThrowIfNull(discoveryProbe); ArgumentNullException.ThrowIfNull(usage);
        _discoveryProbe = discoveryProbe; _usage = usage;
        ArgumentNullException.ThrowIfNull(clients); ArgumentNullException.ThrowIfNull(upstreams); ArgumentNullException.ThrowIfNull(officialAccounts);
        ArgumentNullException.ThrowIfNull(siteIdentity); ArgumentNullException.ThrowIfNull(certificates); ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(discovery); ArgumentNullException.ThrowIfNull(clock);
        _clients = clients; _upstreams = upstreams; _officialAccounts = officialAccounts; _siteIdentity = siteIdentity;
        _authorityPem = certificates.Authority.ExportCertificatePem();
        _authoritySha256 = Convert.ToHexStringLower(SHA256.HashData(certificates.Authority.RawData));
        _endpoint = endpoint; _discovery = discovery; _clock = clock;
        _clients.Subscribe(OnClientsChanged);
        retirements.Subscribe(id => CloseAccount(id, SiyuCloseReason.AccountUnavailable));
        _sweep = clock.CreateTimer(_ => Sweep(), null, SiyuContract.SweepInterval, SiyuContract.SweepInterval);
    }

    /// <summary>本服务进程的实例编号。</summary>
    public string ServerInstanceId { get; } = RandomNumberGenerator.GetHexString(32, lowercase: true);

    /// <summary>只核对个人令牌。</summary>
    public async Task AuthorizeAsync(string? personalToken) => _ = await AuthenticateAsync(personalToken);

    /// <summary>凭个人令牌列出能用于思域的官方账户（判据 <see cref="Usable"/>）。</summary>
    public async Task<IReadOnlyList<SiyuAccountOption>> ListAccountsAsync(string? personalToken)
    {
        _ = await AuthenticateAsync(personalToken);
        return (await _upstreams.ListCandidatesAsync()).Where(Usable).Select(item => new SiyuAccountOption(item.Id, item.Name)).ToArray();
    }

    /// <summary>凭个人令牌为一个官方账户建会话；幂等重放、上限、身份代次、资料与凭据都核对过才发票据。</summary>
    public async Task<SiyuSessionCreated> CreateAsync(string? personalToken, SiyuCreateRequest request, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);
        var access = await AuthenticateAsync(personalToken);
        Validate(request);
        var key = (access.UserId!.Value, access.TokenId!.Value, request.RequestId);
        // 幂等重放与人数上限先在锁内判一次，免得为一个注定被拒的请求去读资料、取凭据。
        lock (_sync)
        {
            if (Replay(key, request) is { } replayed) return replayed;
            if (ActiveCount(access.UserId.Value) >= SiyuContract.MaxSessionsPerUser) throw TooMany();
        }
        var account = await ReadAccountAsync(request.OfficialAccountId);
        SiteIdentitySnapshot identity; CancellationToken stopping; SiyuLaunchProfile profile;
        using (EnterOrRefuse())
        {
            identity = _siteIdentity.Current;
            stopping = _siteIdentity.TransportStopping;
            if (request.ExpectedIdentityVersion is { } expected && expected != identity.Version)
                throw new SiyuRefusal(409, "identity-version-stale", "中转机的设备身份已经更新，这份思域配置需要重新准备。请重新连接思域。") { CurrentIdentityVersion = identity.Version };
            profile = _officialAccounts.ReadLaunchProfile(account.Uid);
        }
        // 确认此刻有可用凭据（可能等一次续期）；不占站点许可、不占锁。
        _ = await _officialAccounts.AcquireCredentialAsync(account.Uid, cancellation);
        var ticket = Base64Url(RandomNumberGenerator.GetBytes(32));
        SiyuSession session; SiyuSessionCreated created;
        // 提交前在同一把锁里再核对令牌、身份代次与重放、上限，核对与登记之间不会插进撤销或另一次同键创建。
        lock (_sync)
        {
            if (Replay(key, request) is { } replayed) return replayed;
            if (!_clients.IsCurrent(access.UserId.Value, access.TokenId.Value)) throw Refuse(SiyuCloseReason.TokenInvalid, 401);
            var now = _siteIdentity.Current;
            if (stopping.IsCancellationRequested || now.Pending || now.Version != identity.Version) throw Refuse(SiyuCloseReason.Maintenance, 503);
            if (ActiveCount(access.UserId.Value) >= SiyuContract.MaxSessionsPerUser) throw TooMany();
            var sessionId = RandomNumberGenerator.GetHexString(32, lowercase: true);
            var binding = new SiyuBinding(ServerInstanceId, sessionId, access.UserId.Value, access.TokenId.Value, access.TokenVersion ?? 0, request.OfficialAccountId,
                identity.Version, profile.ProfileVersion, request.ClientProtocolVersion, request.ClaudeVersion, request.Entrypoint);
            var at = _clock.GetUtcNow();
            session = new SiyuSession(binding, account.Uid, profile.AccountUuid, identity.DeviceId, SHA256.HashData(Encoding.UTF8.GetBytes(ticket)), at + SiyuContract.LeaseDuration);
            _sessions[sessionId] = session;
            created = new SiyuSessionCreated(View(session),
                new SiyuLaunchIdentity(identity.DeviceId, profile.OAuthAccountJson, profile.CredentialMetadataJson),
                new SiyuProxyAccess(_endpoint.Address.ToString(), "basic-session-ticket", ticket, _authorityPem, _authoritySha256));
            _creates[key] = new SiyuCreateRecord(request, created, at + SiyuContract.CreateReplayRetention);
        }
        // 锁外挂维护回调：令牌若已取消，Register 会当场同步回调，进锁关闭会话。
        session.Maintenance = stopping.Register(() => CloseSession(session, SiyuCloseReason.Maintenance));
        return created;
    }

    /// <summary>查询自己的会话（同一使用者任一当前有效令牌）；不续租。</summary>
    public async Task<SiyuSessionView> ReadAsync(string? personalToken, string sessionId)
    {
        var access = await AuthenticateAsync(personalToken);
        ExpireIfDue(sessionId);
        // 只读视图，同锁读到完整状态。
        lock (_sync) return View(Owned(access, sessionId));
    }

    /// <summary>续租：只认创建它的那一版令牌；顺带核对资料代次，变了就结束会话。</summary>
    public async Task<SiyuSessionView> RenewAsync(string? personalToken, string sessionId, long leaseSequence)
    {
        var access = await AuthenticateAsync(personalToken);
        if (leaseSequence < 1) throw new SiyuRefusal(400, "request-invalid", "续租序号必须从 1 开始。请升级 MyCoAI 后再试。");
        ExpireIfDue(sessionId);
        SiyuSession session;
        // 先在锁内确认是本人这一版令牌、会话还活着，再去锁外核对资料。
        lock (_sync)
        {
            session = Owned(access, sessionId);
            if (session.Binding.TokenId != access.TokenId) throw NotFound();
            if (session.State != SiyuSessionState.Active) throw Ended(session);
        }
        string current;
        try { current = _officialAccounts.ReadLaunchProfile(session.AccountUid).ProfileVersion; }
        // 资料读不了：维护按维护收口，其余（账户没了、资料坏了、要重新登录）按账户不可用收口，再把原拒绝交给调用方读出。
        catch (SiyuRefusal refusal)
        {
            CloseSession(session, refusal.Code == "site-maintenance" ? SiyuCloseReason.Maintenance : SiyuCloseReason.AccountUnavailable);
            throw;
        }
        if (!string.Equals(current, session.Binding.ProfileVersion, StringComparison.Ordinal))
        {
            _discovery.Invalidate(session.Binding.OfficialAccountId);
            CloseSession(session, SiyuCloseReason.ProfileChanged);
        }
        // 序号比较与延长同锁；同序号或更小只回现值，不重复延长。
        lock (_sync)
        {
            if (session.State != SiyuSessionState.Active) throw Ended(session);
            if (leaseSequence > session.LeaseSequence)
            {
                session.LeaseSequence = leaseSequence;
                session.LeaseExpiresAt = _clock.GetUtcNow() + SiyuContract.LeaseDuration;
            }
            return View(session);
        }
    }

    /// <summary>停止自己的会话（同一使用者任一当前有效令牌）；重复停止幂等。</summary>
    public async Task<SiyuSessionView> CloseAsync(string? personalToken, string sessionId)
    {
        var access = await AuthenticateAsync(personalToken);
        SiyuSession session;
        // 归属判断在锁内，读到的是最新状态。
        lock (_sync) session = Owned(access, sessionId);
        CloseSession(session, SiyuCloseReason.Stopped);
        // 关闭后的视图：资源已归还就是 Closed，否则 Closing。
        lock (_sync) return View(session);
    }

    /// <summary>结束一个官方账户上的全部会话并作废它的 MCP 发现集合。</summary>
    public void CloseAccount(long officialAccountId, SiyuCloseReason reason)
    {
        _discovery.Invalidate(officialAccountId);
        SiyuSession[] affected;
        // 账户收口连同补发现模板一起清掉，换回来的账户要等客户端重新发现。
        lock (_sync) _discoveryTemplates.Remove(officialAccountId);
        // 只在锁内挑出受影响的会话，关闭在锁外逐个做。
        lock (_sync) affected = _sessions.Values.Where(s => s.State == SiyuSessionState.Active && s.Binding.OfficialAccountId == officialAccountId).ToArray();
        foreach (var session in affected) CloseSession(session, reason);
    }

    /// <summary>管理页的会话一览：锁内取快照，按使用者、会话编号排列。</summary>
    public IReadOnlyList<SiyuSessionSummary> List()
    {
        // 计数与状态在锁内读成一份一致的快照。
        lock (_sync)
            return [.. _sessions.Values.OrderBy(s => s.Binding.UserId).ThenBy(s => s.Binding.SessionId, StringComparer.Ordinal)
                .Select(s => new SiyuSessionSummary(s.Binding, s.State, s.LeaseExpiresAt, s.Tunnels, s.Permits, s.Reason))];
    }

    /// <summary>管理员结束一个会话：与使用者停止同一个关闭流程，原因记为管理员结束；找不到什么也不做。</summary>
    public void CloseByAdministrator(string sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        SiyuSession? session;
        // 只在锁内查找，关闭在锁外做。
        lock (_sync) _sessions.TryGetValue(sessionId, out session);
        if (session is not null) CloseSession(session, SiyuCloseReason.AdministratorStopped);
    }

    /// <summary>外层 CONNECT 的准入：票据对得上、会话存活、隧道名额未满才给隧道，否则 null（不说明是哪一项）。</summary>
    public ISiyuTunnel? OpenTunnel(string sessionId, string ticket, OfficialHost host)
    {
        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(ticket)) return null;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(ticket));
        ExpireIfDue(sessionId);
        // 票据比对、状态与名额判断和计数加一在同一把锁里，名额不会被并发打穿。
        lock (_sync)
        {
            if (!_sessions.TryGetValue(sessionId, out var session) || !CryptographicOperations.FixedTimeEquals(hash, session.TicketHash)
                || session.State != SiyuSessionState.Active || session.Tunnels >= SiyuContract.MaxTunnelsPerSession) return null;
            session.Tunnels++;
            return new SiyuTunnel(this, session);
        }
    }

    /// <summary>
    /// 一次内层请求的准入：个人令牌必须是这个会话的那一版；会话存活且租约未到；连接器在当次发现集合里；
    /// 先取有效官方凭据（不占站点许可），再取站点许可并在锁内复核，交出发送许可。任何一步不过抛 <see cref="SiyuRefusal"/>，官方收不到这次请求。
    /// </summary>
    internal async Task<ISiyuPermit> AdmitAsync(SiyuSession session, SiyuRoute route, string bearerToken, CancellationToken cancellation)
    {
        var admittedAt = _clock.GetUtcNow();
        var access = await AuthenticateAsync(bearerToken);
        if (access.TokenId != session.Binding.TokenId) throw Refuse(SiyuCloseReason.TokenInvalid, 401);
        CheckActive(session);
        if (route.Purpose == SiyuPurpose.McpConnector)
        {
            var check = _discovery.Check(session.Binding.OfficialAccountId, session.Binding.ProfileVersion, route.ConnectorId!);
            // 没有有效集合（客户端用的是缓存、这次没主动发现，或集合已过期）：按账户单飞补一次，再查。
            if (check == McpDiscoveryCheck.NotDiscovered)
            {
                await RediscoverAsync(session, cancellation);
                check = _discovery.Check(session.Binding.OfficialAccountId, session.Binding.ProfileVersion, route.ConnectorId!);
            }
            if (check == McpDiscoveryCheck.NotDiscovered)
                throw new SiyuRefusal(403, "mcp-not-discovered", "思域还没拿到这个账户的官方连接器清单，这次连接器请求没有转发。请重新启动思域会话，Claude Code 启动时会重新取清单。");
            if (check == McpDiscoveryCheck.Unknown)
                throw new SiyuRefusal(403, "mcp-connector-unknown", "这个官方连接器不在这个账户当前的连接器列表里，思域没有转发。请在 Claude Code 里重新加载连接器后再试。");
        }
        OfficialCredentialLease credential;
        try { credential = await _officialAccounts.AcquireCredentialAsync(session.AccountUid, cancellation); }
        // 凭据层的拒绝：要重新登录或账户没了，就把这个会话收口，再把原拒绝交给传输层回给客户端。
        catch (SiyuRefusal refusal) when (refusal.Code is "official-reauthentication-required" or "official-expiry-unknown" or "official-account-gone")
        {
            CloseSession(session, refusal.Code == "official-account-gone" ? SiyuCloseReason.AccountUnavailable : SiyuCloseReason.Reauthentication);
            throw;
        }
        var operation = EnterOrRefuse();
        try
        {
            // 复核、拒绝代次判断与许可计数同锁，关闭不会插在复核和计数之间。
            lock (_sync)
            {
                CheckActiveLocked(session);
                if (_siteIdentity.Current.Version != session.Binding.SiteIdentityVersion) throw Refuse(SiyuCloseReason.Maintenance, 503);
                if (_rejectedGenerations.TryGetValue(session.AccountUid, out var rejected) && credential.Generation <= rejected)
                    throw Refuse(SiyuCloseReason.Reauthentication, 503);
                session.Permits++;
            }
        }
        // 没交出许可就归还站点许可，维护排空不会被一个被拒的请求拖住。
        catch { operation.Dispose(); throw; }
        string? requestId = null;
        if (UsageKindOf(route.Purpose) is { } kind)
        {
            requestId = Guid.NewGuid().ToString("N");
            try { await _usage.StartAsync(requestId, access, kind); }
            // 记不下开始记录就不发（与旧接法同一规则）：归还站点许可与会话名额，本地拒绝。
            catch (RelayFailure) { operation.Dispose(); ReleasePermit(session); throw UsageUnavailable(); }
        }
        return new SiyuPermit(this, session, operation, credential, route, admittedAt, CancellationTokenSource.CreateLinkedTokenSource(session.Stop.Token), requestId, access.Name);
    }

    /// <summary>思域用途对应的用量种类：推理、估算、模型目录三种记用量（与旧接法同一套），其余用途不记。</summary>
    internal static UsageKind? UsageKindOf(SiyuPurpose purpose) => purpose switch
    {
        SiyuPurpose.Messages => UsageKind.Inference,
        SiyuPurpose.CountTokens => UsageKind.Estimate,
        SiyuPurpose.Models => UsageKind.Models,
        _ => null,
    };

    /// <summary>写给官方之前把用量记录标记为已发出：版本取账户此刻的上游配置版本，模型取请求体顶层的 model。记不下或账户已不在就本地拒绝。</summary>
    internal async Task DispatchUsageAsync(SiyuSession session, string requestId, ReadOnlyMemory<byte> body)
    {
        UpstreamCandidateView account;
        try { account = await _upstreams.ReadCandidateAsync(session.Binding.OfficialAccountId); }
        // 账户在会话期间被删：这次不发，会话随账户退役收口。
        catch (InvalidOperationException) { throw AccountUnavailable(); }
        try { await _usage.DispatchAsync(requestId, account.ExpectedVersion, session.Binding.OfficialAccountId, ModelOf(body.Span)); }
        // 记不下已发出就不发（与旧接法同一规则）。
        catch (RelayFailure) { throw UsageUnavailable(); }
    }

    /// <summary>
    /// 许可释放时写用量结束：没发出是失败；会话取消且没转完是取消；没转完是中断；官方非 2xx 或错误事件是失败；事件流协议不完整是中断；其余成功。
    /// 用量服务自己重试并计数失败，不抛。
    /// </summary>
    internal async Task FinishUsageAsync(string requestId, UsageAccumulator usage, bool dispatched, int? upstreamStatus, int clientStatus, bool complete, bool cancelled, DateTimeOffset admittedAt)
    {
        var outcome = !dispatched ? UsageOutcome.Failed
            : !complete ? (cancelled ? UsageOutcome.Cancelled : UsageOutcome.Interrupted)
            : upstreamStatus is not (>= 200 and < 300) || usage.Error ? UsageOutcome.Failed
            : usage.IsStream && !usage.ProtocolComplete ? UsageOutcome.Interrupted
            : UsageOutcome.Succeeded;
        var succeeded = outcome == UsageOutcome.Succeeded;
        var money = usage.ReportedMoney is { } reported ? reported with { Completeness = usage.ProtocolComplete && succeeded ? MoneyCompleteness.Complete : MoneyCompleteness.Partial } : null;
        await _usage.FinishAsync(requestId, new UsageEnd(outcome, dispatched, clientStatus, upstreamStatus, (long)(_clock.GetUtcNow() - admittedAt).TotalMilliseconds,
            usage.Completeness(dispatched, succeeded), usage.Tokens, money));
    }

    /// <summary>请求体顶层的 model 字符串；不是 JSON 对象或没有就为 null（只用于用量的单价快照，不影响转发）。</summary>
    private static string? ModelOf(ReadOnlySpan<byte> body)
    {
        if (body.IsEmpty) return null;
        try
        {
            var reader = new System.Text.Json.Utf8JsonReader(body);
            if (!reader.Read() || reader.TokenType != System.Text.Json.JsonTokenType.StartObject) return null;
            while (reader.Read() && reader.TokenType == System.Text.Json.JsonTokenType.PropertyName)
            {
                var isModel = reader.ValueTextEquals("model"u8);
                if (!reader.Read()) return null;
                if (isModel) return reader.TokenType == System.Text.Json.JsonTokenType.String ? reader.GetString() : null;
                reader.Skip();
            }
            return null;
        }
        // 正文不是合法 JSON：没有模型名，转发照旧由官方判断。
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// <summary>
    /// Messages 正文的只读核对（不改任何字节）：设备段必须是这个会话创建时的站点设备编号、账户段必须是这个官方账户的编号；
    /// 不一致或格式不认识就本地拒绝 403，读句说清是哪位使用者、哪一段不对、去 MyCoAI 重新连接。其余用途不核对正文。
    /// </summary>
    internal void VerifyBody(SiyuSession session, SiyuRoute route, ReadOnlySpan<byte> body, string userName)
    {
        if (route.Purpose != SiyuPurpose.Messages) return;
        var (code, part) = DeviceMetadata.Check(body, session.DeviceId, session.AccountUuid) switch
        {
            DeviceMetadataCheck.Match => (null, null),
            DeviceMetadataCheck.DeviceDiffers => ("device-mismatch", "设备段和中转机的设备编号不一致"),
            DeviceMetadataCheck.AccountDiffers => ("account-mismatch", "账户段和这个官方账户的编号不一致"),
            _ => ("identity-unsupported", "里没有可核对的设备与账户信息"),
        };
        if (code is null) return;
        throw new SiyuRefusal(403, code, $"使用者 {userName} 这台电脑发来的请求{part}，思域没有发给官方。请在 MyCoAI 里重新连接思域，让它重新准备这台电脑的配置。");
    }
    /// <summary>用量记不下时的本地拒绝。</summary>
    private static SiyuRefusal UsageUnavailable() => new(503, "usage-unavailable", "中转暂时记不下用量，这次请求没有发给官方。请稍等一会儿再试。");

    /// <summary>请求体读完、写给官方之前再核对一次：会话仍存活、租约未到、令牌仍有效、站点身份代次未变。</summary>
    internal void Recheck(SiyuSession session)
    {
        CheckActive(session);
        if (!_clients.IsCurrent(session.Binding.UserId, session.Binding.TokenId))
        {
            CloseSession(session, SiyuCloseReason.TokenInvalid);
            throw Refuse(SiyuCloseReason.TokenInvalid, 401);
        }
        var identity = _siteIdentity.Current;
        if (identity.Pending || identity.Version != session.Binding.SiteIdentityVersion)
        {
            CloseSession(session, SiyuCloseReason.Maintenance);
            throw Refuse(SiyuCloseReason.Maintenance, 503);
        }
    }

    /// <summary>
    /// 官方最终状态码的归属：非连接器用途回 401/403，记为这个账户这一代凭据被拒，之后同代及更旧的凭据不再发送（新代次照常），不切号、不暂停整个账户；
    /// 连接器的 401/403 归连接器自己，原样回给客户端，不动账户。
    /// </summary>
    internal void Observe(SiyuSession session, SiyuRoute route, OfficialCredentialLease credential, int status)
    {
        if (route.Purpose == SiyuPurpose.McpConnector || status is not (401 or 403)) return;
        // 取最大值与写回同锁，迟到的旧代次 401 不会把记录压低，也不会盖过更新的代次。
        lock (_sync)
            if (!_rejectedGenerations.TryGetValue(session.AccountUid, out var rejected) || credential.Generation > rejected)
                _rejectedGenerations[session.AccountUid] = credential.Generation;
    }

    /// <summary>
    /// 客户端经思域做的一次连接器发现成功了：记下请求模板，按准入时刻整份替换该账户的发现集合（迟于失效、或比现有集合更早开始的结果不生效）。
    /// 只认发现路由；会话已不存活时不登记（它的回应也不会再转给客户端）。
    /// </summary>
    internal void Discovered(SiyuSession session, SiyuRoute route, DateTimeOffset admittedAt, byte[] requestTemplate, IReadOnlyList<string> connectorIds)
    {
        ArgumentNullException.ThrowIfNull(requestTemplate); ArgumentNullException.ThrowIfNull(connectorIds);
        if (route.Purpose != SiyuPurpose.McpServers) return;
        // 存活核对与模板登记同锁，账户收口不会插在中间留下旧账户的模板。
        lock (_sync)
        {
            if (session.State != SiyuSessionState.Active) return;
            _discoveryTemplates[session.Binding.OfficialAccountId] = requestTemplate;
        }
        _discovery.Replace(session.Binding.OfficialAccountId, session.Binding.ProfileVersion, connectorIds, admittedAt);
    }

    /// <summary>按账户单飞补一次发现：没有模板（从没见过客户端真实的发现请求）就什么都不做；调用方的取消只放弃自己的等待。</summary>
    private async Task RediscoverAsync(SiyuSession session, CancellationToken cancellation)
    {
        var accountId = session.Binding.OfficialAccountId;
        Task flight;
        // 查模板、查进行中的那一份与登记新的一份同锁，同一账户不会并发跑两份。
        lock (_sync)
        {
            if (!_discoveryTemplates.TryGetValue(accountId, out var template)) return;
            if (!_discoveryFlights.TryGetValue(accountId, out var running)) _discoveryFlights[accountId] = running = RunDiscoveryAsync(session, template);
            flight = running;
        }
        await flight.WaitAsync(cancellation);
    }

    /// <summary>
    /// 真正补发现的那一份：限时 <see cref="SiyuContract.McpDiscoveryTimeout"/>，先取有效凭据（不占站点许可），再占站点许可重发模板；
    /// 被官方拒过的凭据代次不用。成功就按开始时刻替换集合；失败不新增、不延长。不论成败都从进行表里移除。
    /// </summary>
    private async Task RunDiscoveryAsync(SiyuSession session, byte[] template)
    {
        // 先让出一次，确保调用方已把这一份登记进进行表，结束时的移除不会早于登记。
        await Task.Yield();
        var accountId = session.Binding.OfficialAccountId;
        try
        {
            var startedAt = _clock.GetUtcNow();
            using var limit = new CancellationTokenSource(SiyuContract.McpDiscoveryTimeout, _clock);
            var credential = await _officialAccounts.AcquireCredentialAsync(session.AccountUid, limit.Token);
            // 拒绝代次只在锁内读。
            lock (_sync)
                if (_rejectedGenerations.TryGetValue(session.AccountUid, out var rejected) && credential.Generation <= rejected) return;
            IReadOnlyList<string>? ids;
            using (EnterOrRefuse()) ids = await _discoveryProbe.DiscoverAsync(template, credential.AccessToken, limit.Token);
            if (ids is not null) _discovery.Replace(accountId, session.Binding.ProfileVersion, ids, startedAt);
        }
        // 凭据或维护拒绝：这次补不到，连接器请求照旧按「没发现过」拒绝并读出怎么恢复。
        catch (SiyuRefusal) { }
        // 超时或服务退出：同上，不重试。
        catch (OperationCanceledException) { }
        // 不论成败都移除，下一次缺集合时可以再补。
        finally { lock (_sync) _discoveryFlights.Remove(accountId); }
    }

    /// <summary>隧道释放：归还名额，会话在收尾中且已无占用就转 Closed。</summary>
    internal void ReleaseTunnel(SiyuSession session)
    {
        // 计数与终态转换同锁。
        lock (_sync) { session.Tunnels--; FinishIfDrained(session); }
    }

    /// <summary>发送许可释放：归还名额，会话在收尾中且已无占用就转 Closed。</summary>
    internal void ReleasePermit(SiyuSession session)
    {
        // 计数与终态转换同锁。
        lock (_sync) { session.Permits--; FinishIfDrained(session); }
    }

    /// <summary>服务退出：停清扫，结束全部会话。</summary>
    public void Dispose()
    {
        _sweep.Dispose();
        SiyuSession[] all;
        // 锁内取快照，锁外逐个关闭。
        lock (_sync) all = [.. _sessions.Values];
        foreach (var session in all) CloseSession(session, SiyuCloseReason.ServiceStopping);
    }

    /// <summary>锁内转 Closing 并记原因，锁外取消在途；已在收尾或已结束的不再动（第一次的原因保留）。</summary>
    private void CloseSession(SiyuSession session, SiyuCloseReason reason)
    {
        // 状态转换同锁：并发关闭只有第一个生效。
        lock (_sync)
        {
            if (session.State != SiyuSessionState.Active) return;
            session.State = SiyuSessionState.Closing;
            session.Reason = reason;
        }
        // 锁外取消：取消回调会同步关闭隧道、归还许可，它们要再进这把锁。
        session.Stop.Cancel();
        session.Maintenance.Dispose();
        // 收尾判断同锁。
        lock (_sync) FinishIfDrained(session);
    }

    /// <summary>（锁内调用）收尾中的会话已无隧道、无许可就转 Closed 并记时刻。</summary>
    private void FinishIfDrained(SiyuSession session)
    {
        if (session.State == SiyuSessionState.Closing && session.Tunnels == 0 && session.Permits == 0)
        {
            session.State = SiyuSessionState.Closed;
            session.ClosedAt = _clock.GetUtcNow();
        }
    }

    /// <summary>令牌名单变了：会话绑定的那一版令牌不在或被停用，就关闭会话。</summary>
    private void OnClientsChanged(IReadOnlyList<ClientAccess> current)
    {
        var valid = current.Where(c => c.Enabled && c.UserId is not null && c.TokenId is not null).Select(c => (c.UserId!.Value, c.TokenId!.Value)).ToHashSet();
        SiyuSession[] affected;
        // 锁内挑出受影响的会话，关闭在锁外做（关闭要再进锁、会同步取消隧道）。
        lock (_sync) affected = _sessions.Values.Where(s => s.State == SiyuSessionState.Active && !valid.Contains((s.Binding.UserId, s.Binding.TokenId))).ToArray();
        foreach (var session in affected) CloseSession(session, SiyuCloseReason.TokenInvalid);
    }

    /// <summary>定时清扫：关闭租约到期的会话，丢掉过了保留期的终态和幂等结果。</summary>
    private void Sweep()
    {
        var now = _clock.GetUtcNow();
        SiyuSession[] expired;
        // 挑选与清除过期记录同锁；关闭在锁外做。
        lock (_sync)
        {
            expired = _sessions.Values.Where(s => s.State == SiyuSessionState.Active && now >= s.LeaseExpiresAt).ToArray();
            foreach (var (id, session) in _sessions.ToArray())
                if (session.State == SiyuSessionState.Closed && session.ClosedAt + SiyuContract.ClosedRetention <= now) _sessions.Remove(id);
            foreach (var (key, record) in _creates.ToArray())
                if (record.ExpiresAt <= now) _creates.Remove(key);
        }
        foreach (var session in expired) CloseSession(session, SiyuCloseReason.LeaseExpired);
    }

    /// <summary>不等清扫：这个会话租约已到就当场关闭，准入、查询、续租都先过它。</summary>
    private void ExpireIfDue(string sessionId)
    {
        if (sessionId is null) return;
        SiyuSession? due;
        // 判断到期同锁。
        lock (_sync) due = _sessions.TryGetValue(sessionId, out var session) && session.State == SiyuSessionState.Active && _clock.GetUtcNow() >= session.LeaseExpiresAt ? session : null;
        if (due is not null) CloseSession(due, SiyuCloseReason.LeaseExpired);
    }

    /// <summary>会话仍可准入，否则抛出它的结束原因；租约到了先关闭再抛。</summary>
    private void CheckActive(SiyuSession session)
    {
        ExpireIfDue(session.Binding.SessionId);
        // 读状态同锁。
        lock (_sync) CheckActiveLocked(session);
    }

    /// <summary>（锁内调用）会话不是 Active 或租约已到就抛出。</summary>
    private void CheckActiveLocked(SiyuSession session)
    {
        if (session.State != SiyuSessionState.Active) throw Ended(session);
        if (_clock.GetUtcNow() >= session.LeaseExpiresAt) throw Refuse(SiyuCloseReason.LeaseExpired, 409);
    }

    /// <summary>（锁内调用）找这名使用者自己的会话；不存在或不是他的一律 404，不说明是哪一种。</summary>
    private SiyuSession Owned(ClientAccess access, string sessionId) =>
        sessionId is not null && _sessions.TryGetValue(sessionId, out var session) && session.Binding.UserId == access.UserId ? session : throw NotFound();

    /// <summary>（锁内调用）同键在保留期内创建过：请求相同且会话仍存活就原样返回；请求不同或会话已结束 409；没有返回 null。</summary>
    private SiyuSessionCreated? Replay((long, long, string) key, SiyuCreateRequest request)
    {
        if (!_creates.TryGetValue(key, out var record) || record.ExpiresAt <= _clock.GetUtcNow()) return null;
        if (record.Request != request) throw new SiyuRefusal(409, "request-conflict", "同一个请求编号带了不同的内容，思域没有创建会话。请升级 MyCoAI 后重新启动。");
        var session = _sessions.GetValueOrDefault(record.Created.Session.Binding.SessionId);
        if (session is null || session.State != SiyuSessionState.Active) throw new SiyuRefusal(409, "session-closed", SiyuCloseReasons.Sentence(SiyuCloseReason.LeaseExpired));
        return record.Created;
    }

    /// <summary>（锁内调用）这名使用者存活中的会话数。</summary>
    private int ActiveCount(long userId) => _sessions.Values.Count(s => s.State == SiyuSessionState.Active && s.Binding.UserId == userId);

    /// <summary>（锁内调用）当前视图。</summary>
    private static SiyuSessionView View(SiyuSession session) => new(session.Binding, session.State, session.LeaseExpiresAt, (int)SiyuContract.LeaseRenewAfter.TotalSeconds,
        session.Reason is { } reason ? SiyuCloseReasons.Code(reason) : null, session.Reason is { } spoken ? SiyuCloseReasons.Sentence(spoken) : null);

    /// <summary>个人令牌鉴权；读库失败按维护类 503 拒绝，令牌无效 401。旧内存夹具没有主键的名单项不能用于思域。</summary>
    private async Task<ClientAccess> AuthenticateAsync(string? personalToken)
    {
        ClientAccess? access;
        try { access = await _clients.AuthenticateAsync(personalToken); }
        // 名单读不到时整站失败即拒绝，这里换成思域的读句。
        catch (RelayFailure) { throw new SiyuRefusal(503, "access-database-unavailable", "中转机暂时读不到使用者名单，思域没有处理这次请求。请稍后再试。"); }
        if (access?.UserId is null || access.TokenId is null) throw Refuse(SiyuCloseReason.TokenInvalid, 401);
        return access;
    }

    /// <summary>读要绑定的官方账户：必须是 <see cref="Usable"/> 的那一家。</summary>
    private async Task<UpstreamCandidateView> ReadAccountAsync(long officialAccountId)
    {
        UpstreamCandidateView account;
        try { account = await _upstreams.ReadCandidateAsync(officialAccountId); }
        // 不存在或已停用：上游服务的读句是给管理员的，这里换成给使用者的。
        catch (InvalidOperationException) { throw AccountUnavailable(); }
        if (!Usable(account)) throw AccountUnavailable();
        return account;
    }

    /// <summary>能用于思域的官方账户判据，唯一定义处（建会话与账户列表共用）：未损坏、官方登录、未导出、检测已通过。</summary>
    private static bool Usable(UpstreamCandidateView account)
        => !account.Damaged && account.Options.OfficialLogin && account.ExportedAt is null && account.State == CandidateState.Passed;

    /// <summary>取站点官方操作许可；维护中按维护读句拒绝。</summary>
    private IDisposable EnterOrRefuse()
    {
        try { return _siteIdentity.Enter(); }
        // 维护中：官方收不到请求，按维护读句拒绝。
        catch (SiteIdentityUnavailableException) { throw Refuse(SiyuCloseReason.Maintenance, 503); }
    }

    /// <summary>请求字段核对：协议、传输、版本、入口不在已验证能力档里 426；格式不对 400。</summary>
    private static void Validate(SiyuCreateRequest request)
    {
        static bool Token(string? value, int max, Func<char, bool> allowed) => value is { Length: > 0 } && value.Length <= max && value.All(allowed);
        if (!Token(request.RequestId, 64, c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') || request.OfficialAccountId <= 0
            || !Token(request.BinarySha256, 64, char.IsAsciiHexDigitLower) || request.BinarySha256.Length != 64)
            throw new SiyuRefusal(400, "request-invalid", "思域启动请求的格式不对。请升级 MyCoAI 后再试。");
        if (request.ClientProtocolVersion != SiyuContract.ProtocolVersion || !string.Equals(request.Transport, SiyuContract.Transport, StringComparison.Ordinal))
            throw new SiyuRefusal(426, "protocol-unsupported", "这个 MyCoAI 的思域协议与中转机不一致。请把 MyCoAI 与中转更新到配套版本。");
        if (!SiyuContract.SupportedClaudeVersions.Contains(request.ClaudeVersion, StringComparer.Ordinal)
            || !SiyuContract.SupportedEntrypoints.Contains(request.Entrypoint, StringComparer.Ordinal))
            throw new SiyuRefusal(426, "version-unsupported", "此 Claude Code 版本尚未通过思域兼容验证。请使用已支持版本或选择其他启动方式。");
    }

    /// <summary>按结束原因的固定码与读句拒绝。</summary>
    private static SiyuRefusal Refuse(SiyuCloseReason reason, int status) => new(status, SiyuCloseReasons.Code(reason), SiyuCloseReasons.Sentence(reason));

    /// <summary>会话已结束：带上它的结束原因。</summary>
    private static SiyuRefusal Ended(SiyuSession session) => Refuse(session.Reason ?? SiyuCloseReason.Stopped, 409);

    /// <summary>会话不存在或不属于你。</summary>
    private static SiyuRefusal NotFound() => new(404, "session-not-found", "找不到这个思域会话，可能已经结束很久了。请重新启动思域。");

    /// <summary>人数上限。</summary>
    private static SiyuRefusal TooMany() => new(429, "too-many-sessions", $"你同时开着的思域已经有 {SiyuContract.MaxSessionsPerUser} 个。请先关掉不用的，再启动。");

    /// <summary>官方账户不可用。</summary>
    private static SiyuRefusal AccountUnavailable() => Refuse(SiyuCloseReason.AccountUnavailable, 409);

    /// <summary>无填充 base64url。</summary>
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
