using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Application.Contracts.Siyu;
using Application.Interfaces.Siyu;
using Application.Interfaces.Upstream;
using Application.Services.Siyu;
using Core.Configuration;
using Core.Identity;
using Core.Relay;
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
        var created = (SiyuOfficialSessionCreated)await rig.Service.CreateAsync(Alice, request, default);
        var binding = created.Session.Binding;
        Check(created.Session.State == SiyuSessionState.Active && binding.UserId == 1 && binding.TokenId == 11 && binding.TokenVersion == 1 && binding.OfficialAccountId == AccountId
            && binding.SiteIdentityVersion == 1 && binding.ProfileVersion == rig.Profile.ProfileVersion && binding.ServerInstanceId == rig.Service.ServerInstanceId, "绑定取自同一次核对");
        Check(created.LaunchIdentity.DeviceId == new string('d', 64) && created.LaunchIdentity.OAuthAccountJson.Span.SequenceEqual(rig.Profile.OAuthAccountJson.Span), "启动身份是站点设备编号与原字节资料");
        Check(created.Proxy.Ticket.Length >= 43 && created.Proxy.Endpoint == "https://relay.example.test:8789/" && created.Proxy.AuthoritySha256.Length == 64
            && created.Proxy.AuthorityPem.StartsWith("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal), "代理接入齐全");
        Check(created.Session.LeaseExpiresAt == rig.Clock.GetUtcNow() + SiyuContract.LeaseDuration && created.Session.RenewAfterSeconds == 40, "租约 120 秒，40 秒后续租");

        var replay = (SiyuOfficialSessionCreated)await rig.Service.CreateAsync(Alice, request, default);
        Check(ReferenceEquals(replay, created), "同一 requestId 重试原样返回");
        await Refused(() => rig.Service.CreateAsync(Alice, request with { Entrypoint = "sdk-cli", BinarySha256 = new string('b', 64) }, default), 409, "request-conflict", "同键不同内容");

        var id = binding.SessionId;
        Check((await rig.Service.ReadAsync(Alice, id)).State == SiyuSessionState.Active, "本人可查");
        await Refused(() => rig.Service.ReadAsync(Bob, id), 404, "session-not-found", "别人查不到");

        rig.Clock.Advance(TimeSpan.FromSeconds(100));
        var renewed = await rig.Service.RenewAsync(Alice, id, 1);
        Check(renewed.LeaseExpiresAt == rig.Clock.GetUtcNow() + SiyuContract.LeaseDuration, "序号 1 续租延长");
        rig.Clock.Advance(TimeSpan.FromSeconds(10));
        Check((await rig.Service.RenewAsync(Alice, id, 1)).LeaseExpiresAt == renewed.LeaseExpiresAt, "同序号重试不重复延长");
        await Refused(() => rig.Service.RenewAsync(Alice, id, 0), 400, "request-invalid", "序号从 1 开始");

        var closed = await rig.Service.CloseAsync(Alice, id);
        Check(closed.State == SiyuSessionState.Closed && closed.CloseCode == "stopped" && closed.CloseSentence!.Length > 0, "没有隧道直接 Closed");
        Check((await rig.Service.CloseAsync(Alice, id)).State == SiyuSessionState.Closed, "重复停止幂等");
        await Refused(() => rig.Service.RenewAsync(Alice, id, 2), 409, "stopped", "结束后不能续租");
        await Refused(() => rig.Service.CreateAsync(Alice, request, default), 409, "session-closed", "已结束的会话不因重放复活");
    }

    /// <summary>
    /// 账户列表：未知令牌 401；只列与建会话同一判据能用的官方账户（未通过检测、已导出、不是官方登录、损坏的都不列），按名单先后，只有主键与名称；一个都没有是空表。
    /// 代理凭据字符集：每次建会话发的 sessionId 是 32 位小写十六进制、ticket 是 43 位 base64url，两样都只含 [A-Za-z0-9_-]，转义后原样不变。
    /// </summary>
    public static async Task AccountsAndProxyCredentialAlphabet()
    {
        using var rig = new Rig();
        await Refused(() => rig.Service.ListAccountsAsync("hr_unknown_token_value"), 401, "personal-token-invalid", "未知令牌");
        try { await rig.Service.AuthorizeAsync(null); Check(false, "没带令牌应拒绝"); }
        // 预期的 401：与建会话同一个原因码。
        catch (SiyuRefusal refusal) { Check(refusal.Status == 401 && refusal.Code == "personal-token-invalid", "没带令牌 401"); }
        await rig.Service.AuthorizeAsync(Alice);
        var spare = rig.Account with { Id = 43, Uid = "spare", Name = "备用官方账户" };
        rig.Candidates =
        [
            rig.Account,
            rig.Account with { Id = 50, State = CandidateState.Untested },
            rig.Account with { Id = 51, ExportedAt = DateTimeOffset.UtcNow },
            rig.Account with { Id = 52, Options = new UpstreamOptions() },
            rig.Account with { Id = 53, Damaged = true },
            spare,
        ];
        var listed = await rig.Service.ListAccountsAsync(Bob);
        Check(listed.SequenceEqual([new SiyuAccountOption(AccountId, "家里的官方账户"), new SiyuAccountOption(43, "备用官方账户")]), "只列能用的两家，主键与名称，按名单先后");
        rig.Candidates = [];
        Check((await rig.Service.ListAccountsAsync(Alice)).Count == 0, "一个都没有是空表");

        var alphabet = new System.Text.RegularExpressions.Regex("^[" + SiyuContract.ProxyCredentialAlphabet + "]+$");
        for (var i = 0; i < SiyuContract.MaxSessionsPerUser; i++)
        {
            var created = (SiyuOfficialSessionCreated)await rig.Service.CreateAsync(Alice, Request("alphabet-" + i), default);
            var (id, ticket) = (created.Session.Binding.SessionId, created.Proxy.Ticket);
            Check(id.Length == 32 && id.All(char.IsAsciiHexDigitLower) && ticket.Length == 43, $"第 {i} 次：会话编号 32 位小写十六进制、票据 43 位");
            Check(alphabet.IsMatch(id) && alphabet.IsMatch(ticket), $"第 {i} 次：两样只含 [A-Za-z0-9_-]");
            Check(Uri.EscapeDataString(id) == id && Uri.EscapeDataString(ticket) == ticket, $"第 {i} 次：转义后原样不变");
        }
    }

    /// <summary>令牌、协议、版本、入口、传输、身份代次、账户、维护、人数上限的拒绝。</summary>
    public static async Task CreateRefusals()
    {
        using var rig = new Rig();
        await Refused(() => rig.Service.CreateAsync("hr_unknown_token_value", Request("a"), default), 401, "personal-token-invalid", "未知令牌");
        await Refused(() => rig.Service.CreateAsync(Alice, Request("b") with { ClaudeVersion = "2.1.999" }, default), 426, "version-unsupported", "未验证版本");
        await Refused(() => rig.Service.CreateAsync(Alice, Request("c") with { Entrypoint = "cli" }, default), 426, "version-unsupported", "未验证入口");
        await Refused(() => rig.Service.CreateAsync(Alice, Request("d") with { Transport = "loopbackTunnel" }, default), 426, "protocol-unsupported", "版本 1 只有原生代理");
        await Refused(() => rig.Service.CreateAsync(Alice, Request("e") with { ClientProtocolVersion = 3, OfficialAccountId = null }, default), 426, "protocol-unsupported", "协议版本");
        await Refused(() => rig.Service.CreateAsync(Alice, Request("f") with { BinarySha256 = "XYZ" }, default), 400, "request-invalid", "哈希格式");
        try { await rig.Service.CreateAsync(Alice, Request("g") with { ExpectedIdentityVersion = 7 }, default); Check(false, "过时代次应拒绝"); }
        // 预期的 409：必须带上服务端当前代次。
        catch (SiyuRefusal refusal) { Check(refusal.Status == 409 && refusal.Code == "identity-version-stale" && refusal.CurrentIdentityVersion == 1, "过时代次 409 带当前代次"); }

        rig.Account = rig.Account with { State = CandidateState.Untested };
        await Refused(() => rig.Service.CreateAsync(Alice, Request("h"), default), 409, "official-account-unavailable", "未通过检测");
        rig.Account = rig.Account with { State = CandidateState.Passed, ExportedAt = DateTimeOffset.UtcNow };
        await Refused(() => rig.Service.CreateAsync(Alice, Request("i"), default), 409, "official-account-unavailable", "已导出");
        rig.Account = rig.Account with { ExportedAt = null, Options = new UpstreamOptions() };
        await Refused(() => rig.Service.CreateAsync(Alice, Request("j"), default), 409, "official-account-unavailable", "不是官方登录");
        rig.Account = rig.Account with { Options = new UpstreamOptions { OfficialLogin = true } };

        rig.Site.Maintain();
        await Refused(() => rig.Service.CreateAsync(Alice, Request("k"), default), 503, "site-maintenance", "维护中");
        rig.Site.Reopen(1);

        for (var i = 0; i < SiyuContract.MaxSessionsPerUser; i++) await rig.Service.CreateAsync(Alice, Request("n" + i), default);
        await Refused(() => rig.Service.CreateAsync(Alice, Request("over"), default), 429, "too-many-sessions", "每人上限");
        Check((await rig.Service.CreateAsync(Bob, Request("bob"), default)).Session.Binding.UserId == 2, "别人不受影响");
    }

    /// <summary>
    /// 连接器发现：客户端真实发现成功才登记集合与模板；非发现路由的报告不算；没有模板时不自己拼请求；集合失效后同一账户单飞补一次（模板原样、官方令牌），
    /// 等待者各自取消不影响别人；补不到就拒绝并不新增；账户收口清掉模板。
    /// </summary>
    public static async Task DiscoveryIsLearnedAndRediscovered()
    {
        using var rig = new Rig();
        var created = (SiyuOfficialSessionCreated)await rig.Service.CreateAsync(Alice, Request("m"), default);
        var tunnel = rig.Service.OpenTunnel(created.Session.Binding.SessionId, created.Proxy.Ticket, OfficialHost.Api)!;
        var connector = new SiyuRoute(SiyuPurpose.McpConnector, "mcpsrv_1");
        await Refused(() => tunnel.AdmitAsync(connector, Alice, default), 403, "mcp-not-discovered", "没有模板时拒绝");
        Check(rig.ProbeCalls.Count == 0, "没见过客户端真实的发现请求就不自己拼请求");

        var template = "GET /v1/mcp_servers?limit=1000 HTTP/1.1\r\nHost: api.anthropic.com\r\nAuthorization: Bearer siyu-template\r\n\r\n"u8.ToArray();
        await using ((await tunnel.AdmitAsync(new SiyuRoute(SiyuPurpose.Messages, null), Alice, default)).AsAsyncDisposable(out var other)) other.ReportDiscovery(template, ["mcpsrv_9"]);
        await Refused(() => tunnel.AdmitAsync(new SiyuRoute(SiyuPurpose.McpConnector, "mcpsrv_9"), Alice, default), 403, "mcp-not-discovered", "非发现路由的报告不登记");
        await using ((await tunnel.AdmitAsync(new SiyuRoute(SiyuPurpose.McpServers, null), Alice, default)).AsAsyncDisposable(out var discovery)) discovery.ReportDiscovery(template, ["mcpsrv_1"]);
        await using ((await tunnel.AdmitAsync(connector, Alice, default)).AsAsyncDisposable(out _)) { }
        Check(rig.ProbeCalls.Count == 0, "有效集合在时不补发现");

        rig.Discovery.Invalidate(AccountId); rig.Clock.Advance(TimeSpan.FromSeconds(1));
        var gate = new TaskCompletionSource<IReadOnlyList<string>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.ProbeResult = () => gate.Task;
        var first = tunnel.AdmitAsync(connector, Alice, default);
        var second = tunnel.AdmitAsync(connector, Alice, default);
        using (var leave = new CancellationTokenSource())
        {
            var third = tunnel.AdmitAsync(connector, Alice, leave.Token);
            await WaitUntil(() => rig.ProbeCalls.Count >= 1);
            leave.Cancel();
            try { await third; Check(false, "取消的等待者应放弃"); }
            // 预期：只有这个等待者放弃，补发现照常进行。
            catch (OperationCanceledException) { }
        }
        gate.SetResult(["mcpsrv_1"]);
        await using ((await first).AsAsyncDisposable(out _)) { }
        await using ((await second).AsAsyncDisposable(out _)) { }
        Check(rig.ProbeCalls.Count == 1 && rig.ProbeCalls[0].Template.SequenceEqual(template) && rig.ProbeCalls[0].AccessToken == "official-token-g1", "同一账户只补一次，模板原样、用官方令牌");

        rig.Discovery.Invalidate(AccountId); rig.Clock.Advance(TimeSpan.FromSeconds(1));
        rig.ProbeResult = () => Task.FromResult<IReadOnlyList<string>?>(null);
        await Refused(() => tunnel.AdmitAsync(connector, Alice, default), 403, "mcp-not-discovered", "补不到就拒绝");
        Check(rig.ProbeCalls.Count == 2, "失败后下一次缺集合可以再补");

        rig.Service.CloseAccount(AccountId, SiyuCloseReason.AccountUnavailable);
        var again = (SiyuOfficialSessionCreated)await rig.Service.CreateAsync(Alice, Request("m2"), default);
        var fresh = rig.Service.OpenTunnel(again.Session.Binding.SessionId, again.Proxy.Ticket, OfficialHost.Api)!;
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        await Refused(() => fresh.AdmitAsync(connector, Alice, default), 403, "mcp-not-discovered", "收口后的新会话没有集合");
        Check(rig.ProbeCalls.Count == 2, "账户收口清掉了模板，不再补发现");
    }

    /// <summary>
    /// 用量：推理准入写开始（使用者与令牌、种类），已发出写账户版本、账户主键与请求体里的模型，释放写结束（结果按官方状态与完整度判定、时长按时钟）；
    /// 非用量用途一条不记；开始或已发出写不进就本地拒绝 503 并归还站点许可。
    /// </summary>
    public static async Task UsageIsRecordedLikeTheOldPath()
    {
        using var rig = new Rig();
        var created = (SiyuOfficialSessionCreated)await rig.Service.CreateAsync(Alice, Request("u"), default);
        var tunnel = rig.Service.OpenTunnel(created.Session.Binding.SessionId, created.Proxy.Ticket, OfficialHost.Api)!;
        var messages = new SiyuRoute(SiyuPurpose.Messages, null);
        await using (var permit = await tunnel.AdmitAsync(messages, Alice, default))
        {
            await permit.DispatchingAsync(MessagesBody(new string('d', 64), ProfileAccountUuid), default);
            permit.Usage!.Json("{\"type\":\"message\",\"usage\":{\"input_tokens\":4,\"output_tokens\":2}}"u8.ToArray());
            rig.Clock.Advance(TimeSpan.FromMilliseconds(1500));
            permit.Observe(200); permit.Completed(200, true);
        }
        var calls = rig.UsageCalls;
        Check(calls.Select(call => call.Method).SequenceEqual([nameof(Application.Interfaces.Usage.IUsageService.StartAsync), nameof(Application.Interfaces.Usage.IUsageService.DispatchAsync), nameof(Application.Interfaces.Usage.IUsageService.FinishAsync)]), "开始、已发出、结束各一次");
        var id = (string)calls[0].Args[0]!;
        Check(((ClientAccess)calls[0].Args[1]!).TokenId == 11 && (Core.Usage.UsageKind)calls[0].Args[2]! == Core.Usage.UsageKind.Inference, "开始记的是这名使用者这一版令牌、推理");
        Check((string)calls[1].Args[0]! == id && (long)calls[1].Args[1]! == rig.Account.ExpectedVersion && (long?)calls[1].Args[2] == AccountId && (string?)calls[1].Args[3] == "claude-haiku-5-5",
            "已发出记账户版本、账户主键与模型");
        var end = (Core.Usage.UsageEnd)calls[2].Args[1]!;
        Check((string)calls[2].Args[0]! == id && end.Outcome == Core.Usage.UsageOutcome.Succeeded && end.Dispatched && end.Status == 200 && end.UpstreamStatus == 200
            && end.DurationMs == 1500 && end.Tokens.Input == 4 && end.Completeness == Core.Usage.UsageCompleteness.Complete, "结束记成功、计数、时长与完整度");

        calls.Clear();
        await using (var permit = await tunnel.AdmitAsync(messages, Alice, default))
        {
            await permit.DispatchingAsync(MessagesBody(new string('d', 64), ProfileAccountUuid), default);
            permit.Observe(200); permit.Completed(200, false);
        }
        Check(((Core.Usage.UsageEnd)calls[^1].Args[1]!).Outcome == Core.Usage.UsageOutcome.Interrupted, "回应没转完记中断");
        calls.Clear();
        await using (var permit = await tunnel.AdmitAsync(messages, Alice, default)) { permit.Observe(529); permit.Completed(529, true); }
        Check(calls.Count == 2 && ((Core.Usage.UsageEnd)calls[^1].Args[1]!).Outcome == Core.Usage.UsageOutcome.Failed && !((Core.Usage.UsageEnd)calls[^1].Args[1]!).Dispatched, "没发出记失败");

        calls.Clear();
        await using (var permit = await tunnel.AdmitAsync(new SiyuRoute(SiyuPurpose.Settings, null), Alice, default))
        {
            await permit.DispatchingAsync("{}"u8.ToArray(), default);
            Check(permit.Usage is null, "远程设置没有累加器");
        }
        Check(calls.Count == 0, "非用量用途一条不记");

        rig.UsageFails = nameof(Application.Interfaces.Usage.IUsageService.StartAsync);
        await Refused(() => tunnel.AdmitAsync(messages, Alice, default), 503, "usage-unavailable", "开始写不进");
        Check(rig.Site.Active == 0, "开始写不进时站点许可已归还");
        rig.UsageFails = nameof(Application.Interfaces.Usage.IUsageService.DispatchAsync);
        var held = await tunnel.AdmitAsync(messages, Alice, default);
        try { await held.DispatchingAsync(MessagesBody(new string('d', 64), ProfileAccountUuid), default); Check(false, "已发出写不进应拒绝"); }
        // 预期的本地拒绝。
        catch (SiyuRefusal refusal) { Check(refusal.Code == "usage-unavailable", "已发出写不进回 usage-unavailable"); }
        await held.DisposeAsync();
        Check(rig.Site.Active == 0, "释放后站点许可归还");
        Check((await rig.Service.CloseAsync(Alice, created.Session.Binding.SessionId)).State == SiyuSessionState.Closing, "隧道还开着时停止是 Closing");
        tunnel.Dispose();
        Check((await rig.Service.ReadAsync(Alice, created.Session.Binding.SessionId)).State == SiyuSessionState.Closed, "许可都归还了，隧道一放就 Closed");
    }

    /// <summary>管理页一览：按使用者列出会话与隧道、在途计数；管理员结束后隧道立即取消、原因记为管理员结束，找不到的编号什么也不做。</summary>
    public static async Task AdministratorListsAndStopsSessions()
    {
        using var rig = new Rig();
        var bob = (SiyuOfficialSessionCreated)await rig.Service.CreateAsync(Bob, Request("b"), default);
        var alice = (SiyuOfficialSessionCreated)await rig.Service.CreateAsync(Alice, Request("a"), default);
        var tunnel = rig.Service.OpenTunnel(alice.Session.Binding.SessionId, alice.Proxy.Ticket, OfficialHost.Api)!;
        var listed = rig.Service.List();
        Check(listed.Select(s => s.Binding.UserId).SequenceEqual([1L, 2L]) && listed[0].Tunnels == 1 && listed[0].State == SiyuSessionState.Active && listed[0].CloseReason is null, "按使用者排列，带隧道计数");
        rig.Service.CloseByAdministrator("no-such-session");
        rig.Service.CloseByAdministrator(alice.Session.Binding.SessionId);
        Check(tunnel.Closed.IsCancellationRequested, "管理员结束即取消隧道");
        var after = rig.Service.List().Single(s => s.Binding.UserId == 1);
        Check(after.State == SiyuSessionState.Closing && after.CloseReason == SiyuCloseReason.AdministratorStopped, "原因记为管理员结束");
        Check(rig.Service.List().Single(s => s.Binding.UserId == 2).State == SiyuSessionState.Active, "别人的会话不受影响");
        tunnel.Dispose();
        var view = await rig.Service.ReadAsync(Alice, alice.Session.Binding.SessionId);
        Check(view.State == SiyuSessionState.Closed && view.CloseCode == "stopped-by-administrator", "使用者查询读到管理员结束");
        _ = bob;
    }

    /// <summary>
    /// Messages 正文只读核对：两段都对才放行（内嵌 JSON 与旧格式都认，转义也认）；设备段不对、账户段为空或不对、没有 metadata 都本地 403，
    /// 读句带使用者名与哪一段；被拒时用量不记已发出；估算不核对正文。
    /// </summary>
    public static async Task MessagesIdentityIsVerifiedReadOnly()
    {
        using var rig = new Rig();
        var created = (SiyuOfficialSessionCreated)await rig.Service.CreateAsync(Alice, Request("v"), default);
        var tunnel = rig.Service.OpenTunnel(created.Session.Binding.SessionId, created.Proxy.Ticket, OfficialHost.Api)!;
        var messages = new SiyuRoute(SiyuPurpose.Messages, null);
        var device = new string('d', 64);
        async Task Expect(byte[] body, string? code, string label)
        {
            rig.UsageCalls.Clear();
            await using var permit = await tunnel.AdmitAsync(messages, Alice, default);
            try
            {
                await permit.DispatchingAsync(body, default);
                Check(code is null, label + "：应拒绝 " + code);
                Check(rig.UsageCalls.Any(call => call.Method == nameof(Application.Interfaces.Usage.IUsageService.DispatchAsync)), label + "：放行后记已发出");
            }
            // 预期的本地拒绝：核对原因码、读句与用量。
            catch (SiyuRefusal refusal)
            {
                Check(refusal.Status == 403 && refusal.Code == code && refusal.Sentence.Contains("alice", StringComparison.Ordinal) && refusal.Sentence.Contains("MyCoAI", StringComparison.Ordinal),
                    $"{label}：应是 403 {code}，实际 {refusal.Status} {refusal.Code}");
                Check(rig.UsageCalls.All(call => call.Method != nameof(Application.Interfaces.Usage.IUsageService.DispatchAsync)), label + "：被拒时不记已发出");
            }
        }
        await Expect(MessagesBody(device, ProfileAccountUuid), null, "两段都对");
        await Expect(Encoding.UTF8.GetBytes("{\"metadata\":{\"user_id\":\"user_" + device + "_account_" + ProfileAccountUuid + "_session_x\"},\"model\":\"m\"}"), null, "旧格式两段都对");
        await Expect(Encoding.UTF8.GetBytes("{\"metadata\":{\"user_id\":\"{\\\"device_id\\\":\\\"" + device + "\\\",\\\"account_uuid\\\":\\\"\\u0030f6c2a1e-3b7d-4c55-9a10-2f4e8d9b1c01\\\"}\"}}"), null, "转义写法两段都对");
        await Expect(MessagesBody(new string('e', 64), ProfileAccountUuid), "device-mismatch", "设备段不对");
        await Expect(MessagesBody(device, ""), "account-mismatch", "账户段为空（没配好的客户端）");
        await Expect(MessagesBody(device, "11111111-2222-3333-4444-555555555555"), "account-mismatch", "账户段是别的账户");
        await Expect("{\"model\":\"m\",\"messages\":[]}"u8.ToArray(), "identity-unsupported", "没有 metadata");
        await Expect(Encoding.UTF8.GetBytes("{\"metadata\":{\"user_id\":\"a\"},\"metadata\":{\"user_id\":\"b\"}}"), "identity-unsupported", "重复 metadata");
        rig.UsageCalls.Clear();
        await using (var permit = await tunnel.AdmitAsync(new SiyuRoute(SiyuPurpose.CountTokens, null), Alice, default))
            await permit.DispatchingAsync("{\"model\":\"m\",\"messages\":[]}"u8.ToArray(), default);
        Check(rig.UsageCalls.Any(call => call.Method == nameof(Application.Interfaces.Usage.IUsageService.DispatchAsync)), "估算不核对正文，照常记已发出");
    }

    /// <summary>测试台资料里的账户编号。</summary>
    private const string ProfileAccountUuid = "0f6c2a1e-3b7d-4c55-9a10-2f4e8d9b1c01";

    /// <summary>带内嵌 JSON 格式 user_id 的 Messages 正文（与 2.1.325 实测格式同形）。</summary>
    private static byte[] MessagesBody(string device, string account) => Encoding.UTF8.GetBytes(
        "{\"model\":\"claude-haiku-5-5\",\"max_tokens\":1,\"messages\":[],\"metadata\":{\"user_id\":" + System.Text.Json.JsonSerializer.Serialize("{\"device_id\":\"" + device + "\",\"account_uuid\":\"" + account + "\",\"session_id\":\"s-1\"}") + "}}");

    /// <summary>轮询等条件成立，最多 5 秒。</summary>
    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition()) { Check(DateTime.UtcNow < deadline, "等待超时"); await Task.Delay(10); }
    }

    /// <summary>隧道票据与名额、准入的令牌代次、连接器发现集合、被拒凭据代次、停止时 Closing 到 Closed。</summary>
    public static async Task TunnelsAndAdmission()
    {
        using var rig = new Rig();
        var created = (SiyuOfficialSessionCreated)await rig.Service.CreateAsync(Alice, Request("t"), default);
        var id = created.Session.Binding.SessionId; var ticket = created.Proxy.Ticket;
        Check(rig.Service.OpenTunnel(id, ticket + "x", OfficialHost.Api) is null && rig.Service.OpenTunnel("0000", ticket, OfficialHost.Api) is null, "票据或会话不对一律 null");
        var tunnels = Enumerable.Range(0, SiyuContract.MaxTunnelsPerSession).Select(_ => rig.Service.OpenTunnel(id, ticket, OfficialHost.Api)).ToArray();
        Check(tunnels.All(t => t is not null) && rig.Service.OpenTunnel(id, ticket, OfficialHost.Api) is null, "每会话 16 条");
        tunnels[^1]!.Dispose(); tunnels[^1]!.Dispose();
        var reopened = rig.Service.OpenTunnel(id, ticket, OfficialHost.Api);
        Check(reopened is not null && rig.Service.OpenTunnel(id, ticket, OfficialHost.Api) is null, "归还一条（重复释放只算一次）后只能再开一条");
        var tunnel = tunnels[0]!;

        var messages = new SiyuRoute(SiyuPurpose.Messages, null);
        await Refused(() => tunnel.AdmitAsync(messages, Bob, default), 401, "personal-token-invalid", "别人的令牌");
        await using (var permit = await tunnel.AdmitAsync(messages, Alice, default))
        {
            Check(permit.AccessToken == "official-token-g1" && !permit.Cancellation.IsCancellationRequested && rig.Site.Active == 1, "准入拿到官方令牌并占一个站点许可");
            permit.Recheck();
            permit.Observe(401);
        }
        Check(rig.Site.Active == 0, "许可释放即归还站点许可");
        await Refused(() => tunnel.AdmitAsync(messages, Alice, default), 503, "official-reauthentication-required", "同代凭据被官方拒过不再发");
        rig.Generation = 2;
        await using ((await tunnel.AdmitAsync(messages, Alice, default)).AsAsyncDisposable(out var renewed)) Check(renewed.AccessToken == "official-token-g2", "新代次照常发送");

        var connector = new SiyuRoute(SiyuPurpose.McpConnector, "mcpsrv_1");
        await Refused(() => tunnel.AdmitAsync(connector, Alice, default), 403, "mcp-not-discovered", "没有发现集合");
        rig.Discovery.Replace(AccountId, rig.Profile.ProfileVersion, ["mcpsrv_1"], rig.Clock.GetUtcNow());
        await using ((await tunnel.AdmitAsync(connector, Alice, default)).AsAsyncDisposable(out var mcp)) mcp.Observe(401);
        await using ((await tunnel.AdmitAsync(messages, Alice, default)).AsAsyncDisposable(out _)) { }
        await Refused(() => tunnel.AdmitAsync(new SiyuRoute(SiyuPurpose.McpConnector, "mcpsrv_2"), Alice, default), 403, "mcp-connector-unknown", "集合外的连接器");

        var held = await tunnel.AdmitAsync(messages, Alice, default);
        var view = await rig.Service.CloseAsync(Alice, id);
        Check(view.State == SiyuSessionState.Closing && tunnel.Closed.IsCancellationRequested && held.Cancellation.IsCancellationRequested, "有隧道与在途许可时停止是 Closing，且都已取消");
        Assert(() => held.Recheck(), "stopped");
        await held.DisposeAsync();
        foreach (var t in tunnels.Take(SiyuContract.MaxTunnelsPerSession - 1)) t!.Dispose();
        reopened!.Dispose();
        Check((await rig.Service.ReadAsync(Alice, id)).State == SiyuSessionState.Closed, "全部归还后 Closed");
        Check(rig.Service.OpenTunnel(id, ticket, OfficialHost.Api) is null, "结束后不能再开隧道");
    }

    /// <summary>撤销或轮换令牌即断会话；新版令牌可查可停旧会话但不能续租；维护即断。</summary>
    public static async Task RevocationAndMaintenanceDropSessions()
    {
        using var rig = new Rig();
        var created = (SiyuOfficialSessionCreated)await rig.Service.CreateAsync(Alice, Request("r"), default);
        var id = created.Session.Binding.SessionId;
        var tunnel = rig.Service.OpenTunnel(id, created.Proxy.Ticket, OfficialHost.Api)!;
        rig.Clients.Replace([Access("alice", AliceRotated, 1, 12, 2), Access("bob", Bob, 2, 21, 1)]);
        Check(tunnel.Closed.IsCancellationRequested, "轮换后隧道立即取消");
        await Refused(() => rig.Service.ReadAsync(Alice, id), 401, "personal-token-invalid", "旧令牌已失效");
        var view = await rig.Service.ReadAsync(AliceRotated, id);
        Check(view.State == SiyuSessionState.Closing && view.CloseCode == "personal-token-invalid", "新版令牌可查旧会话终态");
        await Refused(() => rig.Service.RenewAsync(AliceRotated, id, 5), 404, "session-not-found", "新版令牌不能续租旧会话");
        tunnel.Dispose();
        Check((await rig.Service.CloseAsync(AliceRotated, id)).State == SiyuSessionState.Closed, "新版令牌可结束旧会话");

        var second = (SiyuOfficialSessionCreated)await rig.Service.CreateAsync(AliceRotated, Request("m"), default);
        var secondTunnel = rig.Service.OpenTunnel(second.Session.Binding.SessionId, second.Proxy.Ticket, OfficialHost.McpProxy)!;
        rig.Site.Maintain();
        Check(secondTunnel.Closed.IsCancellationRequested, "维护开始即断隧道");
        secondTunnel.Dispose();
        var maintained = await rig.Service.ReadAsync(AliceRotated, second.Session.Binding.SessionId);
        Check(maintained.State == SiyuSessionState.Closed && maintained.CloseCode == "site-maintenance", "维护结束会话");
        rig.Site.Reopen(2);
        Check(rig.Service.OpenTunnel(second.Session.Binding.SessionId, second.Proxy.Ticket, OfficialHost.Api) is null, "维护后不复活");
        Check((await rig.Service.CreateAsync(AliceRotated, Request("after"), default)).Session.Binding.SiteIdentityVersion == 2, "维护后新建会话绑定新代次");
    }

    /// <summary>租约到点不再准入（不等清扫）；清扫关闭过期会话；终态保留期过后查不到。</summary>
    public static async Task LeaseExpiry()
    {
        using var rig = new Rig();
        var created = (SiyuOfficialSessionCreated)await rig.Service.CreateAsync(Alice, Request("l"), default);
        var id = created.Session.Binding.SessionId; var ticket = created.Proxy.Ticket;
        rig.Clock.Set(rig.Clock.GetUtcNow() + SiyuContract.LeaseDuration - TimeSpan.FromTicks(1));
        var tunnel = rig.Service.OpenTunnel(id, ticket, OfficialHost.Api);
        Check(tunnel is not null, "到期前一刻仍可开隧道");
        rig.Clock.Set(rig.Clock.GetUtcNow() + TimeSpan.FromTicks(1));
        Check(rig.Service.OpenTunnel(id, ticket, OfficialHost.Api) is null && tunnel!.Closed.IsCancellationRequested, "到期那一刻起不准入并断开隧道");
        tunnel!.Dispose();
        Check((await rig.Service.ReadAsync(Alice, id)).CloseCode == "lease-expired", "原因是租约到期");

        var other = (SiyuOfficialSessionCreated)await rig.Service.CreateAsync(Alice, Request("sweep"), default);
        rig.Clock.Advance(SiyuContract.LeaseDuration + SiyuContract.SweepInterval);
        var swept = await rig.Service.ReadAsync(Alice, other.Session.Binding.SessionId);
        Check(swept.State == SiyuSessionState.Closed && swept.CloseCode == "lease-expired", "清扫关闭过期会话");
        rig.Clock.Advance(SiyuContract.ClosedRetention + SiyuContract.SweepInterval);
        await Refused(() => rig.Service.ReadAsync(Alice, other.Session.Binding.SessionId), 404, "session-not-found", "终态保留期过后查不到");
    }

    /// <summary>续租时资料换代：结束会话并作废发现集合；只改取资料时间不算换代。</summary>
    public static async Task ProfileChangeEndsSessionOnRenew()
    {
        using var rig = new Rig();
        var created = (SiyuOfficialSessionCreated)await rig.Service.CreateAsync(Alice, Request("p"), default);
        var id = created.Session.Binding.SessionId;
        rig.Discovery.Replace(AccountId, rig.Profile.ProfileVersion, ["mcpsrv_1"], rig.Clock.GetUtcNow());
        rig.Profile = Rig.MakeProfile("\"billingType\":\"stripe_subscription\",\"profileFetchedAt\":2");
        Check((await rig.Service.RenewAsync(Alice, id, 1)).State == SiyuSessionState.Active, "只改取资料时间不换代");
        rig.Profile = Rig.MakeProfile("\"billingType\":\"apple_subscription\",\"profileFetchedAt\":3");
        await Refused(() => rig.Service.RenewAsync(Alice, id, 2), 409, "profile-changed", "资料换代结束会话");
        Check(rig.Discovery.Check(AccountId, created.Session.Binding.ProfileVersion, "mcpsrv_1") == McpDiscoveryCheck.NotDiscovered, "发现集合同时作废");
        rig.Profile = Rig.MakeProfile(null, missing: true);
        var next = await Task.Run(async () =>
        {
            try { await rig.Service.CreateAsync(Alice, Request("q"), default); return "created"; }
            // 预期的资料拒绝。
            catch (SiyuRefusal refusal) { return refusal.Code; }
        });
        Check(next == "profile-missing", "没有资料的账户建不了会话：" + next);
    }

    /// <summary>账户退役（删除或搬走）只结束这个账户上的会话，并作废它的发现集合；别的账户不受影响。</summary>
    public static async Task AccountRetirementEndsItsSessions()
    {
        using var rig = new Rig();
        var created = (SiyuOfficialSessionCreated)await rig.Service.CreateAsync(Alice, Request("x"), default);
        var tunnel = rig.Service.OpenTunnel(created.Session.Binding.SessionId, created.Proxy.Ticket, OfficialHost.Api)!;
        rig.Discovery.Replace(AccountId, rig.Profile.ProfileVersion, ["mcpsrv_1"], rig.Clock.GetUtcNow());
        rig.Retirements.Retire(AccountId + 1);
        Check(!tunnel.Closed.IsCancellationRequested, "别的账户退役不影响");
        rig.Retirements.Retire(AccountId);
        Check(tunnel.Closed.IsCancellationRequested, "本账户退役即断隧道");
        tunnel.Dispose();
        var view = await rig.Service.ReadAsync(Alice, created.Session.Binding.SessionId);
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
        var id = await service.LoginOfficialAsync((await service.ReadActiveAsync()).ActiveVersion, new UpstreamOptions { BaseUrl = "https://api.anthropic.com" }, "家里的官方账户", default);
        var keep = await service.SaveCandidateAsync((await service.ReadActiveAsync()).ActiveVersion, new UpstreamOptions { BaseUrl = "https://api.example.com" }, "fixture-api-key-value", "独立 API");
        await service.DeleteAsync(keep);
        Check(retired.Count == 0, "删独立 API 上游不发布退役");
        await service.DeleteAsync(id);
        Check(retired.SequenceEqual([id]), "删官方登录上游按主键发布一次退役");
    }

    /// <summary>一个合法的创建请求。</summary>
    private static SiyuCreateRequest Request(string requestId) =>
        new(requestId, null, SiyuContract.LegacyProtocolVersion, "2.1.325", new string('a', 64), "sdk-cli", SiyuContract.Transport, AccountId);

    /// <summary>名单项。</summary>
    private static ClientAccess Access(string name, string token, long userId, long tokenId, int version) =>
        new(name, ConfigurationFiles.Hash(token), true, userId, tokenId, version);

    /// <summary>
    /// 版本 2 后端选择：不带账户建会话，在用的是官方账户就绑它（官方模式、有票据）；管理员把在用换成独立 API 后，官方会话续租以 account-switched 结束并打回 recreate；
    /// 重建得到标准模式（只有站点标准 API 地址，没有票据与身份，开不了隧道）；再换回官方，标准会话同样 account-switched。每次打回都留痕，不记令牌与票据。
    /// </summary>
    public static async Task Version2FollowsBackendChoice()
    {
        using var rig = new Rig();
        var official = await rig.Service.CreateAsync(Alice, Request2("v2-a"), default);
        Check(official is SiyuOfficialSessionCreated { Session.Binding.OfficialAccountId: AccountId, Session.Binding.ClientProtocolVersion: 2 } && official.Session.Binding.Mode == SiyuMode.Official,
            "在用的是能用于思域的官方账户：官方模式绑它");
        var first = (SiyuOfficialSessionCreated)official;
        Check(rig.Service.OpenTunnel(first.Session.Binding.SessionId, first.Proxy.Ticket, OfficialHost.Api) is { } tunnel && Release(tunnel), "官方模式照常开隧道");
        Check((await rig.Service.RenewAsync(Alice, first.Session.Binding.SessionId, 1)).State == SiyuSessionState.Active, "后端没换：续租照常");

        rig.Active = new UpstreamOverview(2, new UpstreamOptions(), true, null, 77, "API 供应商");
        var switched = await RefusedWith(() => rig.Service.RenewAsync(Alice, first.Session.Binding.SessionId, 2), 409, "account-switched", "换成标准上游后续租");
        Check(switched.Remedy == new SiyuRemedy(SiyuRemedyAction.Recreate) && switched.CurrentIdentityVersion is null, "account-switched 打回 recreate，不带别的值");
        Check((await rig.Service.ReadAsync(Alice, first.Session.Binding.SessionId)).CloseCode == "account-switched", "会话的结束原因记成 account-switched");

        var standard = await rig.Service.CreateAsync(Alice, Request2("v2-b"), default);
        Check(standard is SiyuStandardSessionCreated { Standard: { BaseUrl: "https://relay.example.test:8787/", Protocol: "anthropic-messages", Authentication: "personal-token" } }
            && standard.Session.Binding.Mode == SiyuMode.Standard && standard.Session.Binding.OfficialAccountId is null, "重建得到标准模式：站点标准 API 地址与个人令牌认证");
        var standardId = standard.Session.Binding.SessionId;
        Check(rig.Service.OpenTunnel(standardId, "", OfficialHost.Api) is null, "标准模式没有票据，开不了隧道");
        Check((await rig.Service.RenewAsync(Alice, standardId, 1)).State == SiyuSessionState.Active, "标准之间换家不算换：续租照常");
        rig.Active = rig.Active with { UpstreamCandidateId = 78, Name = "另一家 API" };
        Check((await rig.Service.RenewAsync(Alice, standardId, 2)).State == SiyuSessionState.Active, "在用的换成另一家独立 API：标准会话不受影响（旧接法自己跟着在用走）");

        rig.Active = new UpstreamOverview(3, new UpstreamOptions { OfficialLogin = true }, true, null, AccountId, "家里的官方账户");
        await RefusedWith(() => rig.Service.RenewAsync(Alice, standardId, 3), 409, "account-switched", "换回官方后标准会话续租");
        Check(await rig.Service.CreateAsync(Alice, Request2("v2-c"), default) is SiyuOfficialSessionCreated, "再重建回到官方模式");

        var spare = rig.Account with { Id = 43, Uid = "spare", Name = "备用官方账户" };
        var bound = (SiyuOfficialSessionCreated)await rig.Service.CreateAsync(Alice, Request2("v2-d"), default);
        rig.Active = rig.Active with { UpstreamCandidateId = 43 };
        rig.Account = spare;
        await RefusedWith(() => rig.Service.RenewAsync(Alice, bound.Session.Binding.SessionId, 1), 409, "account-switched", "换成另一个官方账户后续租");

        var traces = rig.Logger.Entries.Where(entry => entry.Event == "SiyuRemedy").ToArray();
        Check(traces.Length == 3 && traces.All(entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Warning && (string?)entry.Fields["Code"] == "account-switched"
            && (string?)entry.Fields["Remedy"] == "recreate" && (long?)entry.Fields["UserId"] == 1 && (long?)entry.Fields["TokenId"] == 11 && entry.Fields["SessionId"] is string), "每次打回留一条痕：使用者、令牌主键、会话、原因码、下一步");
        Check(rig.Logger.Entries.SelectMany(entry => entry.Fields.Values).OfType<string>().All(value => !value.Contains(Alice, StringComparison.Ordinal) && value != first.Proxy.Ticket), "留痕不带令牌与票据");
    }

    /// <summary>
    /// 版本 2 的打回与拒绝：带账户 400；身份代次过时打回 recreate 并只在打回里给代次；后端没有可用上游 503 不打回；维护中打回 wait 30 秒；
    /// 中转重启后找不到会话按请求声明的版本打回；账户退役记 account-switched；资料换代打回；身份不符当次 403、续租时 identity-mismatch；隧道满读出真实原因。
    /// </summary>
    public static async Task Version2Remedies()
    {
        using var rig = new Rig();
        await Refused(() => rig.Service.CreateAsync(Alice, Request2("x") with { OfficialAccountId = AccountId }, default), 400, "request-invalid", "版本 2 不许带账户");
        var stale = await RefusedWith(() => rig.Service.CreateAsync(Alice, Request2("stale") with { ExpectedIdentityVersion = 7 }, default), 409, "identity-version-stale", "版本 2 过时代次");
        Check(stale.Remedy == new SiyuRemedy(SiyuRemedyAction.Recreate, ExpectedIdentityVersion: 1) && stale.CurrentIdentityVersion is null, "代次只放在打回里");

        rig.Account = rig.Account with { State = CandidateState.Untested };
        var none = await RefusedWith(() => rig.Service.CreateAsync(Alice, Request2("none"), default), 503, "no-usable-upstream", "在用的官方账户不能用于思域");
        Check(none.Remedy is null, "没有可用上游要人处理，不打回");
        rig.Active = rig.Active with { Configured = false };
        await Refused(() => rig.Service.CreateAsync(Alice, Request2("none2"), default), 503, "no-usable-upstream", "没配置在用上游");
        rig.Account = rig.Account with { State = CandidateState.Passed };
        rig.Active = rig.Active with { Configured = true };

        rig.Site.Maintain();
        var wait = await RefusedWith(() => rig.Service.CreateAsync(Alice, Request2("maint"), default), 503, "site-maintenance", "维护中");
        Check(wait.Remedy == new SiyuRemedy(SiyuRemedyAction.Wait, RetryAfterSeconds: 30), "维护中打回 wait 30 秒");
        rig.Site.Reopen(1);

        var gone = await RefusedWith(() => rig.Service.RenewAsync(Alice, "0123456789abcdef0123456789abcdef", 1, 2), 404, "session-not-found", "重启后找不到会话（版本 2）");
        Check(gone.Remedy == new SiyuRemedy(SiyuRemedyAction.Recreate), "找不到会话打回 recreate");
        Check((await RefusedWith(() => rig.Service.RenewAsync(Alice, "0123456789abcdef0123456789abcdef", 1), 404, "session-not-found", "版本 1")).Remedy is null, "版本 1 找不到会话不打回");

        var retired = (SiyuOfficialSessionCreated)await rig.Service.CreateAsync(Alice, Request2("retire"), default);
        rig.Retirements.Retire(AccountId);
        Check((await RefusedWith(() => rig.Service.RenewAsync(Alice, retired.Session.Binding.SessionId, 1), 409, "account-switched", "账户退役")).Remedy is not null, "版本 2 账户退役记 account-switched 并打回");

        var profiled = (SiyuOfficialSessionCreated)await rig.Service.CreateAsync(Alice, Request2("profile"), default);
        rig.Profile = Rig.MakeProfile("\"billingType\":\"other\",\"profileFetchedAt\":1");
        Check((await RefusedWith(() => rig.Service.RenewAsync(Alice, profiled.Session.Binding.SessionId, 1), 409, "profile-changed", "资料换代")).Remedy == new SiyuRemedy(SiyuRemedyAction.Recreate), "资料换代打回 recreate");
        rig.Profile = Rig.MakeProfile("\"billingType\":\"stripe_subscription\",\"profileFetchedAt\":1");

        var mismatched = (SiyuOfficialSessionCreated)await rig.Service.CreateAsync(Alice, Request2("mismatch"), default);
        var session = mismatched.Session.Binding.SessionId;
        using (var tunnel = rig.Service.OpenTunnel(session, mismatched.Proxy.Ticket, OfficialHost.Api)!)
        {
            await using (var permit = await tunnel.AdmitAsync(new SiyuRoute(SiyuPurpose.Messages, null), Alice, default))
            {
                try { await permit.DispatchingAsync(MessagesBody(new string('e', 64), "0f6c2a1e-3b7d-4c55-9a10-2f4e8d9b1c01"), default); Check(false, "设备段不符应拒绝"); }
                // 预期：当次照旧 403，会话还活着。
                catch (SiyuRefusal refusal) { Check(refusal.Status == 403 && refusal.Code == "device-mismatch" && refusal.Remedy is null, "当次照旧 403 device-mismatch，代理通道不带打回"); }
            }
        }
        Check((await rig.Service.ReadAsync(Alice, session)).State == SiyuSessionState.Active, "身份不符不在当次结束会话");
        Check((await RefusedWith(() => rig.Service.RenewAsync(Alice, session, 1), 409, "identity-mismatch", "身份不符后续租")).Remedy == new SiyuRemedy(SiyuRemedyAction.Recreate), "续租时以 identity-mismatch 结束并打回");

        var full = (SiyuOfficialSessionCreated)await rig.Service.CreateAsync(Bob, Request2("full"), default);
        var tunnels = Enumerable.Range(0, SiyuContract.MaxTunnelsPerSession).Select(_ => rig.Service.OpenTunnel(full.Session.Binding.SessionId, full.Proxy.Ticket, OfficialHost.Api)!).ToArray();
        Assert(() => rig.Service.OpenTunnel(full.Session.Binding.SessionId, full.Proxy.Ticket, OfficialHost.Api), "too-many-tunnels");
        foreach (var each in tunnels) each.Dispose();
        Check(rig.Service.OpenTunnel(full.Session.Binding.SessionId, "wrong-ticket", OfficialHost.Api) is null, "票据不对仍是 null（传输层回 407）");
    }

    /// <summary>
    /// 版本 1 在版本 2 上线后逐字照旧：过时代次带当前代次、不打回；后端换了在用上游不影响版本 1 会话；身份不符续租照常；隧道满仍是 null（407）；
    /// 账户退役仍是 official-account-unavailable；任何拒绝都不带打回，不留打回痕。
    /// </summary>
    public static async Task Version1StaysAsBefore()
    {
        using var rig = new Rig();
        var stale = await RefusedWith(() => rig.Service.CreateAsync(Alice, Request("g") with { ExpectedIdentityVersion = 7 }, default), 409, "identity-version-stale", "版本 1 过时代次");
        Check(stale.CurrentIdentityVersion == 1 && stale.Remedy is null, "版本 1 带当前代次、不带打回");
        await Refused(() => rig.Service.CreateAsync(Alice, Request("n") with { OfficialAccountId = null }, default), 400, "request-invalid", "版本 1 必带账户");

        var created = (SiyuOfficialSessionCreated)await rig.Service.CreateAsync(Alice, Request("v1"), default);
        var id = created.Session.Binding.SessionId;
        rig.Active = new UpstreamOverview(2, new UpstreamOptions(), true, null, 77, "API 供应商");
        Check((await rig.Service.RenewAsync(Alice, id, 1)).State == SiyuSessionState.Active, "后端换成标准上游：版本 1 会话照常续租（版本 1 由客户端选账户）");

        using (var tunnel = rig.Service.OpenTunnel(id, created.Proxy.Ticket, OfficialHost.Api)!)
        await using (var permit = await tunnel.AdmitAsync(new SiyuRoute(SiyuPurpose.Messages, null), Alice, default))
        {
            try { await permit.DispatchingAsync(MessagesBody(new string('e', 64), "0f6c2a1e-3b7d-4c55-9a10-2f4e8d9b1c01"), default); Check(false, "应拒绝"); }
            // 预期的当次 403。
            catch (SiyuRefusal refusal) { Check(refusal.Code == "device-mismatch", "当次 403"); }
        }
        Check((await rig.Service.RenewAsync(Alice, id, 2)).State == SiyuSessionState.Active, "版本 1 身份不符不结束会话");

        var tunnels = Enumerable.Range(0, SiyuContract.MaxTunnelsPerSession).Select(_ => rig.Service.OpenTunnel(id, created.Proxy.Ticket, OfficialHost.Api)!).ToArray();
        Check(rig.Service.OpenTunnel(id, created.Proxy.Ticket, OfficialHost.Api) is null, "版本 1 隧道满仍是 null（407）");
        foreach (var each in tunnels) each.Dispose();

        rig.Retirements.Retire(AccountId);
        var ended = await RefusedWith(() => rig.Service.RenewAsync(Alice, id, 3), 409, "official-account-unavailable", "版本 1 账户退役");
        Check(ended.Remedy is null, "版本 1 不打回");
        Check(!rig.Logger.Entries.Any(entry => entry.Event == "SiyuRemedy"), "版本 1 不留打回痕");
    }

    /// <summary>一个合法的版本 2 创建请求（不带账户）。</summary>
    private static SiyuCreateRequest Request2(string requestId) =>
        new(requestId, null, SiyuContract.ProtocolVersion, "2.1.325", new string('a', 64), "sdk-cli", SiyuContract.Transport);

    /// <summary>期待以指定状态码、原因码的本地拒绝结束，交回拒绝本身供核对打回。</summary>
    private static async Task<SiyuRefusal> RefusedWith<T>(Func<Task<T>> action, int status, string code, string label)
    {
        try { await action(); }
        // 预期的拒绝：核对状态码与原因码后交回。
        catch (SiyuRefusal refusal)
        {
            Check(refusal.Status == status && refusal.Code == code, $"{label}：应是 {status} {code}，实际 {refusal.Status} {refusal.Code}");
            return refusal;
        }
        throw new Exception(label + "：应拒绝");
    }

    /// <summary>释放一条隧道并返回真，供断言里一行写完。</summary>
    private static bool Release(ISiyuTunnel tunnel) { tunnel.Dispose(); return true; }

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
            var upstreams = FakeProxy<IUpstreamService>.Create((method, args) => method.Name switch
            {
                nameof(IUpstreamService.ReadCandidateAsync) => Task.FromResult(Account),
                nameof(IUpstreamService.ListCandidatesAsync) => Task.FromResult(Candidates ?? [Account]),
                nameof(IUpstreamService.ReadOverviewAsync) => Task.FromResult(Active),
                _ => throw new NotSupportedException(method.Name),
            });
            var accounts = FakeProxy<IOfficialAccountLogin>.Create((method, args) => method.Name switch
            {
                nameof(IOfficialAccountLogin.ReadLaunchProfile) => (string)args![0]! == AccountUid ? Profile : throw new InvalidOperationException("wrong account"),
                nameof(IOfficialAccountLogin.AcquireCredentialAsync) => Task.FromResult(new OfficialCredentialLease(AccountUid, "official-token-g" + Generation, Clock.GetUtcNow().AddHours(1), Generation)),
                _ => throw new NotSupportedException(method.Name),
            });
            Service = new SiyuSessionService(Clients, upstreams, accounts, Site, new FixedSiyuProxyCertificates(_authority, new Dictionary<OfficialHost, X509Certificate2>()),
                new SiyuEndpoints(new Uri("https://relay.example.test:8789"), new Uri("https://relay.example.test:8787")), Discovery,
                FakeProxy<IMcpDiscoveryProbe>.Create((method, args) =>
                {
                    // 记下每次补发现用的模板与官方令牌；返回值由用例设定。
                    lock (ProbeCalls) ProbeCalls.Add(((byte[])args![0]!, (string)args[1]!));
                    return ProbeResult();
                }),
                FakeProxy<Application.Interfaces.Usage.IUsageService>.Create((method, args) =>
                {
                    // 记下每次用量调用；开始、已发出可按用例设定为存储故障。
                    lock (UsageCalls) UsageCalls.Add((method.Name, args!));
                    return method.Name switch
                    {
                        nameof(Application.Interfaces.Usage.IUsageService.StartAsync) when UsageFails == nameof(Application.Interfaces.Usage.IUsageService.StartAsync) => Task.FromException(new RelayFailure(503, "Usage storage unavailable; request was not sent.")),
                        nameof(Application.Interfaces.Usage.IUsageService.DispatchAsync) when UsageFails == nameof(Application.Interfaces.Usage.IUsageService.DispatchAsync) => Task.FromException(new RelayFailure(503, "Usage storage unavailable; request was not sent.")),
                        nameof(Application.Interfaces.Usage.IUsageService.StartAsync) or nameof(Application.Interfaces.Usage.IUsageService.DispatchAsync) => Task.CompletedTask,
                        nameof(Application.Interfaces.Usage.IUsageService.FinishAsync) => Task.FromResult(Core.Usage.UsageFinish.Saved),
                        _ => throw new NotSupportedException(method.Name),
                    };
                }), Clock, Retirements, Logger);
        }

        /// <summary>在用上游的概览（版本 2 后端选择用）；默认在用的就是 <see cref="Account"/>。</summary>
        public UpstreamOverview Active { get; set; } = new(1, new UpstreamOptions { OfficialLogin = true }, true, null, AccountId, "家里的官方账户");

        /// <summary>会话服务的日志：记下打回留痕事件。</summary>
        public RecordingLogger<SiyuSessionService> Logger { get; } = new();

        /// <summary>上游名单（账户列表用）；null 时只有 <see cref="Account"/> 一家。</summary>
        public IReadOnlyList<UpstreamCandidateView>? Candidates { get; set; }

        /// <summary>用量服务收到的调用（方法名与参数），按顺序。</summary>
        public List<(string Method, object?[] Args)> UsageCalls { get; } = [];

        /// <summary>设定哪一步用量写入失败（Start 或 Dispatch）；null 都成功。</summary>
        public string? UsageFails { get; set; }

        /// <summary>补发现被调用时用的模板与官方令牌。</summary>
        public List<(byte[] Template, string AccessToken)> ProbeCalls { get; } = [];

        /// <summary>补发现的结果；默认补不到。</summary>
        public Func<Task<IReadOnlyList<string>?>> ProbeResult { get; set; } = () => Task.FromResult<IReadOnlyList<string>?>(null);

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
