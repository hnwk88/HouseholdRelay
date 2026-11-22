using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Application.Contracts.Siyu;
using Application.Interfaces.Siyu;
using Application.Interfaces.Upstream;
using Application.Services.Siyu;
using Core.Configuration;
using Core.Identity;
using Core.Siyu;
using Core.Upstream;
using Microsoft.Extensions.DependencyInjection;
using static Tests.TestSuite;

namespace Tests;

/// <summary>思域会话层：创建与幂等、能力档与代次拒绝、隧道与准入、撤销与维护即断、租约到期、资料换代。全部内存替身，拨时钟不等真时间。</summary>
public static class SiyuSessionTests
{
    private const string Alice = "hr_alice_personal_token_version_1";
    private const string AliceRotated = "hr_alice_personal_token_version_2";
    private const string Bob = "hr_bob_personal_token_version_1";
    private const long AccountId = 42;
    private const string AccountUid = "abcdef0123456789abcdef0123456789";

    /// <summary>创建、重放、查询、续租序号、停止与重复停止。</summary>
    public static async Task CreateReadRenewClose()
    {
        using var rig = new Rig();
        var request = Request("req-1");
        var created = await rig.Service.Create(Alice, request, default);
        var binding = created.Session.Binding;
        Check(created.Session.State == SiyuSessionState.Active && binding.UserId == 1 && binding.TokenId == 11 && binding.TokenVersion == 1 && binding.OfficialAccountId == AccountId
            && binding.SiteIdentityVersion == 1 && binding.ProfileVersion == rig.Profile.ProfileVersion && binding.ServerInstanceId == rig.Service.ServerInstanceId, "绑定取自同一次核对");
        Check(created.LaunchIdentity.DeviceId == new string('d', 64) && created.LaunchIdentity.OAuthAccountJson.Span.SequenceEqual(rig.Profile.OAuthAccountJson.Span), "启动身份是站点设备编号与原字节资料");
        Check(created.Proxy.Ticket.Length >= 43 && created.Proxy.Endpoint == "https://relay.example.test:8789/" && created.Proxy.AuthoritySha256.Length == 64
            && created.Proxy.AuthorityPem.StartsWith("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal), "代理接入齐全");
        Check(created.Session.LeaseExpiresAt == rig.Clock.GetUtcNow() + SiyuContract.LeaseDuration && created.Session.RenewAfterSeconds == 40, "租约 120 秒，40 秒后续租");

        var replay = await rig.Service.Create(Alice, request, default);
        Check(ReferenceEquals(replay, created), "同一 requestId 重试原样返回");
        await Refused(() => rig.Service.Create(Alice, request with { Entrypoint = "sdk-cli", BinarySha256 = new string('b', 64) }, default), 409, "request-conflict", "同键不同内容");

        var id = binding.SessionId;
        Check((await rig.Service.Read(Alice, id)).State == SiyuSessionState.Active, "本人可查");
        await Refused(() => rig.Service.Read(Bob, id), 404, "session-not-found", "别人查不到");

        rig.Clock.Advance(TimeSpan.FromSeconds(100));
        var renewed = await rig.Service.Renew(Alice, id, 1);
        Check(renewed.LeaseExpiresAt == rig.Clock.GetUtcNow() + SiyuContract.LeaseDuration, "序号 1 续租延长");
        rig.Clock.Advance(TimeSpan.FromSeconds(10));
        Check((await rig.Service.Renew(Alice, id, 1)).LeaseExpiresAt == renewed.LeaseExpiresAt, "同序号重试不重复延长");
        await Refused(() => rig.Service.Renew(Alice, id, 0), 400, "request-invalid", "序号从 1 开始");

        var closed = await rig.Service.Close(Alice, id);
        Check(closed.State == SiyuSessionState.Closed && closed.CloseCode == "stopped" && closed.CloseSentence!.Length > 0, "没有隧道直接 Closed");
        Check((await rig.Service.Close(Alice, id)).State == SiyuSessionState.Closed, "重复停止幂等");
        await Refused(() => rig.Service.Renew(Alice, id, 2), 409, "stopped", "结束后不能续租");
        await Refused(() => rig.Service.Create(Alice, request, default), 409, "session-closed", "已结束的会话不因重放复活");
    }

    /// <summary>令牌、协议、版本、入口、传输、身份代次、账户、维护、人数上限的拒绝。</summary>
    public static async Task CreateRefusals()
    {
        using var rig = new Rig();
        await Refused(() => rig.Service.Create("hr_unknown_token_value", Request("a"), default), 401, "personal-token-invalid", "未知令牌");
        await Refused(() => rig.Service.Create(Alice, Request("b") with { ClaudeVersion = "2.1.999" }, default), 426, "version-unsupported", "未验证版本");
        await Refused(() => rig.Service.Create(Alice, Request("c") with { Entrypoint = "cli" }, default), 426, "version-unsupported", "未验证入口");
        await Refused(() => rig.Service.Create(Alice, Request("d") with { Transport = "loopbackTunnel" }, default), 426, "protocol-unsupported", "版本 1 只有原生代理");
        await Refused(() => rig.Service.Create(Alice, Request("e") with { ClientProtocolVersion = 2 }, default), 426, "protocol-unsupported", "协议版本");
        await Refused(() => rig.Service.Create(Alice, Request("f") with { BinarySha256 = "XYZ" }, default), 400, "request-invalid", "哈希格式");
        try { await rig.Service.Create(Alice, Request("g") with { ExpectedIdentityVersion = 7 }, default); Check(false, "过时代次应拒绝"); }
        // 预期的 409：必须带上服务端当前代次。
        catch (SiyuRefusal refusal) { Check(refusal.Status == 409 && refusal.Code == "identity-version-stale" && refusal.CurrentIdentityVersion == 1, "过时代次 409 带当前代次"); }

        rig.Account = rig.Account with { State = CandidateState.Untested };
        await Refused(() => rig.Service.Create(Alice, Request("h"), default), 409, "official-account-unavailable", "未通过检测");
        rig.Account = rig.Account with { State = CandidateState.Passed, ExportedAt = DateTimeOffset.UtcNow };
        await Refused(() => rig.Service.Create(Alice, Request("i"), default), 409, "official-account-unavailable", "已导出");
        rig.Account = rig.Account with { ExportedAt = null, Options = new UpstreamOptions() };
        await Refused(() => rig.Service.Create(Alice, Request("j"), default), 409, "official-account-unavailable", "不是官方登录");
        rig.Account = rig.Account with { Options = new UpstreamOptions { OfficialLogin = true } };

        rig.Site.Maintain();
        await Refused(() => rig.Service.Create(Alice, Request("k"), default), 503, "site-maintenance", "维护中");
        rig.Site.Reopen(1);

        for (var i = 0; i < SiyuContract.MaxSessionsPerUser; i++) await rig.Service.Create(Alice, Request("n" + i), default);
        await Refused(() => rig.Service.Create(Alice, Request("over"), default), 429, "too-many-sessions", "每人上限");
        Check((await rig.Service.Create(Bob, Request("bob"), default)).Session.Binding.UserId == 2, "别人不受影响");
    }

    /// <summary>隧道票据与名额、准入的令牌代次、连接器发现集合、被拒凭据代次、停止时 Closing 到 Closed。</summary>
    public static async Task TunnelsAndAdmission()
    {
        using var rig = new Rig();
        var created = await rig.Service.Create(Alice, Request("t"), default);
        var id = created.Session.Binding.SessionId; var ticket = created.Proxy.Ticket;
        Check(rig.Service.OpenTunnel(id, ticket + "x", OfficialHost.Api) is null && rig.Service.OpenTunnel("0000", ticket, OfficialHost.Api) is null, "票据或会话不对一律 null");
        var tunnels = Enumerable.Range(0, SiyuContract.MaxTunnelsPerSession).Select(_ => rig.Service.OpenTunnel(id, ticket, OfficialHost.Api)).ToArray();
        Check(tunnels.All(t => t is not null) && rig.Service.OpenTunnel(id, ticket, OfficialHost.Api) is null, "每会话 16 条");
        tunnels[^1]!.Dispose(); tunnels[^1]!.Dispose();
        var reopened = rig.Service.OpenTunnel(id, ticket, OfficialHost.Api);
        Check(reopened is not null && rig.Service.OpenTunnel(id, ticket, OfficialHost.Api) is null, "归还一条（重复释放只算一次）后只能再开一条");
        var tunnel = tunnels[0]!;

        var messages = new SiyuRoute(SiyuPurpose.Messages, null);
        await Refused(() => tunnel.Admit(messages, Bob, default), 401, "personal-token-invalid", "别人的令牌");
        await using (var permit = await tunnel.Admit(messages, Alice, default))
        {
            Check(permit.AccessToken == "official-token-g1" && !permit.Cancellation.IsCancellationRequested && rig.Site.Active == 1, "准入拿到官方令牌并占一个站点许可");
            permit.Recheck();
            permit.Observe(401);
        }
        Check(rig.Site.Active == 0, "许可释放即归还站点许可");
        await Refused(() => tunnel.Admit(messages, Alice, default), 503, "official-reauthentication-required", "同代凭据被官方拒过不再发");
        rig.Generation = 2;
        await using ((await tunnel.Admit(messages, Alice, default)).AsAsyncDisposable(out var renewed)) Check(renewed.AccessToken == "official-token-g2", "新代次照常发送");

        var connector = new SiyuRoute(SiyuPurpose.McpConnector, "mcpsrv_1");
        await Refused(() => tunnel.Admit(connector, Alice, default), 403, "mcp-not-discovered", "没有发现集合");
        rig.Discovery.Replace(AccountId, rig.Profile.ProfileVersion, ["mcpsrv_1"], rig.Clock.GetUtcNow());
        await using ((await tunnel.Admit(connector, Alice, default)).AsAsyncDisposable(out var mcp)) mcp.Observe(401);
        await using ((await tunnel.Admit(messages, Alice, default)).AsAsyncDisposable(out _)) { }
        await Refused(() => tunnel.Admit(new SiyuRoute(SiyuPurpose.McpConnector, "mcpsrv_2"), Alice, default), 403, "mcp-connector-unknown", "集合外的连接器");

        var held = await tunnel.Admit(messages, Alice, default);
        var view = await rig.Service.Close(Alice, id);
        Check(view.State == SiyuSessionState.Closing && tunnel.Closed.IsCancellationRequested && held.Cancellation.IsCancellationRequested, "有隧道与在途许可时停止是 Closing，且都已取消");
        Assert(() => held.Recheck(), "stopped");
        await held.DisposeAsync();
        foreach (var t in tunnels.Take(SiyuContract.MaxTunnelsPerSession - 1)) t!.Dispose();
        reopened!.Dispose();
        Check((await rig.Service.Read(Alice, id)).State == SiyuSessionState.Closed, "全部归还后 Closed");
        Check(rig.Service.OpenTunnel(id, ticket, OfficialHost.Api) is null, "结束后不能再开隧道");
    }

    /// <summary>撤销或轮换令牌即断会话；新版令牌可查可停旧会话但不能续租；维护即断。</summary>
    public static async Task RevocationAndMaintenanceDropSessions()
    {
        using var rig = new Rig();
        var created = await rig.Service.Create(Alice, Request("r"), default);
        var id = created.Session.Binding.SessionId;
        var tunnel = rig.Service.OpenTunnel(id, created.Proxy.Ticket, OfficialHost.Api)!;
        rig.Clients.Replace([Access("alice", AliceRotated, 1, 12, 2), Access("bob", Bob, 2, 21, 1)]);
        Check(tunnel.Closed.IsCancellationRequested, "轮换后隧道立即取消");
        await Refused(() => rig.Service.Read(Alice, id), 401, "personal-token-invalid", "旧令牌已失效");
        var view = await rig.Service.Read(AliceRotated, id);
        Check(view.State == SiyuSessionState.Closing && view.CloseCode == "personal-token-invalid", "新版令牌可查旧会话终态");
        await Refused(() => rig.Service.Renew(AliceRotated, id, 5), 404, "session-not-found", "新版令牌不能续租旧会话");
        tunnel.Dispose();
        Check((await rig.Service.Close(AliceRotated, id)).State == SiyuSessionState.Closed, "新版令牌可结束旧会话");

        var second = await rig.Service.Create(AliceRotated, Request("m"), default);
        var secondTunnel = rig.Service.OpenTunnel(second.Session.Binding.SessionId, second.Proxy.Ticket, OfficialHost.McpProxy)!;
        rig.Site.Maintain();
        Check(secondTunnel.Closed.IsCancellationRequested, "维护开始即断隧道");
        secondTunnel.Dispose();
        var maintained = await rig.Service.Read(AliceRotated, second.Session.Binding.SessionId);
        Check(maintained.State == SiyuSessionState.Closed && maintained.CloseCode == "site-maintenance", "维护结束会话");
        rig.Site.Reopen(2);
        Check(rig.Service.OpenTunnel(second.Session.Binding.SessionId, second.Proxy.Ticket, OfficialHost.Api) is null, "维护后不复活");
        Check((await rig.Service.Create(AliceRotated, Request("after"), default)).Session.Binding.SiteIdentityVersion == 2, "维护后新建会话绑定新代次");
    }

    /// <summary>租约到点不再准入（不等清扫）；清扫关闭过期会话；终态保留期过后查不到。</summary>
    public static async Task LeaseExpiry()
    {
        using var rig = new Rig();
        var created = await rig.Service.Create(Alice, Request("l"), default);
        var id = created.Session.Binding.SessionId; var ticket = created.Proxy.Ticket;
        rig.Clock.Set(rig.Clock.GetUtcNow() + SiyuContract.LeaseDuration - TimeSpan.FromTicks(1));
        var tunnel = rig.Service.OpenTunnel(id, ticket, OfficialHost.Api);
        Check(tunnel is not null, "到期前一刻仍可开隧道");
        rig.Clock.Set(rig.Clock.GetUtcNow() + TimeSpan.FromTicks(1));
        Check(rig.Service.OpenTunnel(id, ticket, OfficialHost.Api) is null && tunnel!.Closed.IsCancellationRequested, "到期那一刻起不准入并断开隧道");
        tunnel!.Dispose();
        Check((await rig.Service.Read(Alice, id)).CloseCode == "lease-expired", "原因是租约到期");

        var other = await rig.Service.Create(Alice, Request("sweep"), default);
        rig.Clock.Advance(SiyuContract.LeaseDuration + SiyuContract.SweepInterval);
        var swept = await rig.Service.Read(Alice, other.Session.Binding.SessionId);
        Check(swept.State == SiyuSessionState.Closed && swept.CloseCode == "lease-expired", "清扫关闭过期会话");
        rig.Clock.Advance(SiyuContract.ClosedRetention + SiyuContract.SweepInterval);
        await Refused(() => rig.Service.Read(Alice, other.Session.Binding.SessionId), 404, "session-not-found", "终态保留期过后查不到");
    }

    /// <summary>续租时资料换代：结束会话并作废发现集合；只改取资料时间不算换代。</summary>
    public static async Task ProfileChangeEndsSessionOnRenew()
    {
        using var rig = new Rig();
        var created = await rig.Service.Create(Alice, Request("p"), default);
        var id = created.Session.Binding.SessionId;
        rig.Discovery.Replace(AccountId, rig.Profile.ProfileVersion, ["mcpsrv_1"], rig.Clock.GetUtcNow());
        rig.Profile = Rig.MakeProfile("\"billingType\":\"stripe_subscription\",\"profileFetchedAt\":2");
        Check((await rig.Service.Renew(Alice, id, 1)).State == SiyuSessionState.Active, "只改取资料时间不换代");
        rig.Profile = Rig.MakeProfile("\"billingType\":\"apple_subscription\",\"profileFetchedAt\":3");
        await Refused(() => rig.Service.Renew(Alice, id, 2), 409, "profile-changed", "资料换代结束会话");
        Check(rig.Discovery.Check(AccountId, created.Session.Binding.ProfileVersion, "mcpsrv_1") == McpDiscoveryCheck.NotDiscovered, "发现集合同时作废");
        rig.Profile = Rig.MakeProfile(null, missing: true);
        var next = await Task.Run(async () =>
        {
            try { await rig.Service.Create(Alice, Request("q"), default); return "created"; }
            // 预期的资料拒绝。
            catch (SiyuRefusal refusal) { return refusal.Code; }
        });
        Check(next == "profile-missing", "没有资料的账户建不了会话：" + next);
    }

    /// <summary>账户退役（删除或搬走）只结束这个账户上的会话，并作废它的发现集合；别的账户不受影响。</summary>
    public static async Task AccountRetirementEndsItsSessions()
    {
        using var rig = new Rig();
        var created = await rig.Service.Create(Alice, Request("x"), default);
        var tunnel = rig.Service.OpenTunnel(created.Session.Binding.SessionId, created.Proxy.Ticket, OfficialHost.Api)!;
        rig.Discovery.Replace(AccountId, rig.Profile.ProfileVersion, ["mcpsrv_1"], rig.Clock.GetUtcNow());
        rig.Retirements.Retire(AccountId + 1);
        Check(!tunnel.Closed.IsCancellationRequested, "别的账户退役不影响");
        rig.Retirements.Retire(AccountId);
        Check(tunnel.Closed.IsCancellationRequested, "本账户退役即断隧道");
        tunnel.Dispose();
        var view = await rig.Service.Read(Alice, created.Session.Binding.SessionId);
        Check(view.State == SiyuSessionState.Closed && view.CloseCode == "official-account-unavailable", "原因是账户不可用");
        Check(rig.Discovery.Check(AccountId, rig.Profile.ProfileVersion, "mcpsrv_1") == McpDiscoveryCheck.NotDiscovered, "发现集合作废");
    }

    /// <summary>真实组合根：删除官方登录上游会按它的主键发布退役通知（在删账户目录之前）。</summary>
    public static async Task DeletingOfficialUpstreamRetiresIt()
    {
        using var claude = new FakeClaudeLauncher("success");
        await using var web = await WebFixture.Create(overrides: claude.Register);
        var retired = new List<long>();
        web.App.Services.GetRequiredService<OfficialAccountRetirements>().Subscribe(retired.Add);
        var service = web.Upstreams;
        var id = await service.LoginOfficial((await service.ReadActive()).ActiveVersion, new UpstreamOptions { BaseUrl = "https://api.anthropic.com" }, "家里的官方账户", default);
        var keep = await service.SaveCandidate((await service.ReadActive()).ActiveVersion, new UpstreamOptions { BaseUrl = "https://api.example.com" }, "fixture-api-key-value", "独立 API");
        await service.Delete(keep);
        Check(retired.Count == 0, "删独立 API 上游不发布退役");
        await service.Delete(id);
        Check(retired.SequenceEqual([id]), "删官方登录上游按主键发布一次退役");
    }

    /// <summary>一个合法的创建请求。</summary>
    private static SiyuCreateRequest Request(string requestId) =>
        new(requestId, AccountId, null, SiyuContract.ProtocolVersion, "2.1.325", new string('a', 64), "sdk-cli", SiyuContract.Transport);

    /// <summary>名单项。</summary>
    private static ClientAccess Access(string name, string token, long userId, long tokenId, int version) =>
        new(name, ConfigurationFiles.Hash(token), true, userId, tokenId, version);

    /// <summary>期待以指定状态码、原因码的本地拒绝结束。</summary>
    private static async Task Refused<T>(Func<Task<T>> action, int status, string code, string label)
    {
        try { await action(); Check(false, label + "：应拒绝"); }
        // 预期的拒绝：核对状态码与原因码。
        catch (SiyuRefusal refusal) { Check(refusal.Status == status && refusal.Code == code, $"{label}：应是 {status} {code}，实际 {refusal.Status} {refusal.Code}"); }
    }

    /// <summary>同步操作期待以指定原因码拒绝。</summary>
    private static void Assert(Action action, string code)
    {
        try { action(); Check(false, "应拒绝 " + code); }
        // 预期的拒绝：核对原因码。
        catch (SiyuRefusal refusal) { Check(refusal.Code == code, $"应是 {code}，实际 {refusal.Code}"); }
    }

    /// <summary>让 <c>await using</c> 能直接用准入结果并取出变量。</summary>
    private static IAsyncDisposable AsAsyncDisposable(this ISiyuPermit permit, out ISiyuPermit value) { value = permit; return permit; }

    /// <summary>会话层测试台：内存名单、假上游与官方登录、可控站点身份、可拨时钟。</summary>
    private sealed class Rig : IDisposable
    {
        private readonly X509Certificate2 _authority;

        /// <summary>两名使用者各一版令牌，账户检测已通过，资料合表，凭据代次 1。</summary>
        public Rig()
        {
            Clients = new ClientRegistry([Access("alice", Alice, 1, 11, 1), Access("bob", Bob, 2, 21, 1)]);
            Account = new UpstreamCandidateView(AccountId, AccountUid, "家里的官方账户", 1, new UpstreamOptions { OfficialLogin = true }, CandidateState.Passed, null, true);
            Profile = MakeProfile("\"billingType\":\"stripe_subscription\",\"profileFetchedAt\":1");
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=siyu test proxy authority", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
            _authority = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
            var upstreams = FakeProxy<IUpstreamService>.Create((method, args) => method.Name == nameof(IUpstreamService.ReadCandidate)
                ? Task.FromResult(Account) : throw new NotSupportedException(method.Name));
            var accounts = FakeProxy<IOfficialAccountLogin>.Create((method, args) => method.Name switch
            {
                nameof(IOfficialAccountLogin.ReadLaunchProfile) => (string)args![0]! == AccountUid ? Profile : throw new InvalidOperationException("wrong account"),
                nameof(IOfficialAccountLogin.AcquireCredential) => Task.FromResult(new OfficialCredentialLease(AccountUid, "official-token-g" + Generation, Clock.GetUtcNow().AddHours(1), Generation)),
                _ => throw new NotSupportedException(method.Name),
            });
            Service = new SiyuSessionService(Clients, upstreams, accounts, Site, new FixedSiyuProxyCertificates(_authority, new Dictionary<OfficialHost, X509Certificate2>()),
                new SiyuProxyEndpoint(new Uri("https://relay.example.test:8789")), Discovery, Clock, Retirements);
        }

        /// <summary>令牌名单。</summary>
        public ClientRegistry Clients { get; }

        /// <summary>上游服务读到的账户。</summary>
        public UpstreamCandidateView Account { get; set; }

        /// <summary>官方登录读到的资料；<see cref="MakeProfile"/> 的 missing 版本会让读取抛出 profile-missing。</summary>
        public SiyuLaunchProfile Profile
        {
            get => _profileMissing ? throw new SiyuRefusal(503, "profile-missing", "缺资料") : _profile!;
            set { _profile = value; _profileMissing = value is null; }
        }
        private SiyuLaunchProfile? _profile;
        private bool _profileMissing;

        /// <summary>当前凭据代次。</summary>
        public long Generation { get; set; } = 1;

        /// <summary>可拨时钟。</summary>
        public UsageClock Clock { get; } = new(new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero));

        /// <summary>可控站点身份。</summary>
        public ControlledSiteIdentity Site { get; } = new(new string('d', 64));

        /// <summary>MCP 发现集合。</summary>
        public McpDiscoveryRegistry Discovery => _discovery ??= new McpDiscoveryRegistry(Clock);
        private McpDiscoveryRegistry? _discovery;

        /// <summary>被测服务。</summary>
        public SiyuSessionService Service { get; }

        /// <summary>官方账户退役通知。</summary>
        public OfficialAccountRetirements Retirements { get; } = new();

        /// <summary>按片段做一份合表资料；<paramref name="missing"/> 时返回 null，读取即抛 profile-missing。</summary>
        public static SiyuLaunchProfile MakeProfile(string? extra, bool missing = false) => missing ? null! :
            SiyuLaunchProfileContract.Read(Encoding.UTF8.GetBytes("{\"accountUuid\":\"0f6c2a1e-3b7d-4c55-9a10-2f4e8d9b1c01\",\"emailAddress\":\"a@example.com\"," + extra + "}"),
                """{"scopes":["user:inference"]}"""u8);

        /// <summary>结束服务、释放测试 CA。</summary>
        public void Dispose() { Service.Dispose(); _authority.Dispose(); }
    }
}
