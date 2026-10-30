using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using static Tests.TestSuite;
using static Tests.UsageFixture;

namespace Tests;

/// <summary>
/// 自动切换：两家模拟上游甲（在用）、乙（备用，测试通过），按失败类型换或不换、暂避与自动切回、模型清单跳过、开关关闭、凭据隔离、
/// 用量归属与按那一家计价、页面读出与 v6 架构升级。每个用例用计数器（模拟上游收到的请求）证明哪家收到了几次。
/// </summary>
static class UpstreamFailoverTests
{
    private const string SecretA = "fixture-upstream-a-secret";
    private const string SecretB = "fixture-upstream-b-secret";

    /// <summary>
    /// 甲回 <paramref name="status"/>（503、502、529、429、401 各一条）：换到乙成功，客户端拿到乙的 200；库里同一请求两条尝试，甲「换下一家」、乙「交给客户端」，
    /// 用量记实际服务的乙；控制台事件行读乙、尝试 2 次、最后一次没有错误。
    /// </summary>
    public static async Task Switches(int status)
    {
        await using var scene = await Scene.Create();
        scene.A.Handler = context => Fail(context, status);
        using (var request = Request(scene.Web, scene.Token))
        using (var response = await scene.Web.Http.SendAsync(request))
            Check(response.StatusCode == HttpStatusCode.OK && (await response.Content.ReadAsStringAsync()).Contains("msg_fixture"), $"{status} must be caught by the standby upstream");
        var report = await Settled(scene.Web, 1); var row = report.Rows.Single(); var tries = report.AttemptsOf(row.RequestId);
        Check(row.Outcome == "succeeded" && tries.Count == 2, $"{status}: two attempts");
        Check(tries[0].Number == 1 && tries[0].CandidateId == scene.IdA && tries[0].UpstreamStatus == status && tries[0].Disposition == "next" && tries[0].ErrorType == ErrorType(status), $"{status}: first attempt {tries[0]}");
        Check(tries[1].Number == 2 && tries[1].CandidateId == scene.IdB && tries[1].UpstreamStatus == 200 && tries[1].Disposition == "client" && tries[1].ErrorType is null, $"{status}: second attempt {tries[1]}");
        Check(scene.A.Calls.Count == 1 && scene.B.Calls.Count == 1);
        Check(scene.Served(row.RequestId) == scene.IdB, "Usage belongs to the upstream that actually served");
        var line = scene.Events().Last();
        Check(line.GetProperty("upstream").GetString() == "乙" && line.GetProperty("attempts").GetInt32() == 2 && line.GetProperty("error").ValueKind == JsonValueKind.Null);
    }

    /// <summary>
    /// 在用的那家接受 TCP 却从不回 TLS 握手：15 秒建连超时后换到备用成功，那次记 connection_failed / connect_timeout、去向换下一家。
    /// 甲、乙此时都是备用，按上游页顺序由先保存的甲接住，乙收到 0 次。
    /// </summary>
    public static async Task SwitchesOnConnectTimeout()
    {
        await using var scene = await Scene.Create();
        using var silent = new TcpListener(IPAddress.Loopback, 0); silent.Start();
        var dead = scene.ForceActive(scene.A.Options() with { BaseUrl = "https://127.0.0.1:" + ((IPEndPoint)silent.LocalEndpoint).Port }, "沉默上游");
        using (var request = Request(scene.Web, scene.Token))
        using (var response = await scene.Web.Http.SendAsync(request))
            Check(response.StatusCode == HttpStatusCode.OK, "Connect timeout must be caught by a standby upstream");
        var report = await Settled(scene.Web, 1); var tries = report.AttemptsOf(report.Rows.Single().RequestId);
        Check(tries.Count == 2 && tries[0].CandidateId == dead && tries[0].ErrorType == "connection_failed" && tries[0].ConnectFailure == "connect_timeout"
            && tries[0].Disposition == "next" && tries[1].CandidateId == scene.IdA && tries[1].Disposition == "client", string.Join(" | ", tries));
        Check(scene.A.Calls.Count == 1 && scene.B.Calls.IsEmpty, "Standby upstreams are tried in page order");
    }

    /// <summary>甲回 <paramref name="status"/>（400、404 各一条，请求本身的问题）：不换，客户端拿到甲的原文，乙收到 0 次，库里只有一条「交给客户端」。</summary>
    public static async Task KeepsRequestErrors(int status)
    {
        await using var scene = await Scene.Create();
        var body = Body(status, "fixture request problem");
        scene.A.Handler = context => Raw(context, status, body);
        using (var request = Request(scene.Web, scene.Token))
        using (var response = await scene.Web.Http.SendAsync(request))
            Check((int)response.StatusCode == status && await response.Content.ReadAsStringAsync() == body, $"{status} must reach the client verbatim");
        var report = await Settled(scene.Web, 1); var tries = report.AttemptsOf(report.Rows.Single().RequestId);
        Check(tries.Count == 1 && tries[0].CandidateId == scene.IdA && tries[0].Disposition == "client" && scene.B.Calls.IsEmpty, $"{status} must not switch");
    }

    /// <summary>甲流式回传到一半断开：已经向客户端写出字节，不换；乙收到 0 次，库里只有甲一条、结果为中断或失败。</summary>
    public static async Task KeepsBrokenStream()
    {
        await using var scene = await Scene.Create();
        scene.A.Handler = async context =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync(StartEvent + Delta(4)); await context.Response.Body.FlushAsync();
            // 等中转收到响应头并把第一段转给客户端之后再断开；立刻重置连接会连已发出的字节一起丢掉，那就成了「连不上」而不是回传途中断开。
            await Task.Delay(500); context.Abort();
        };
        using (var request = Request(scene.Web, scene.Token))
        using (var response = await scene.Web.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead))
        {
            Check(response.StatusCode == HttpStatusCode.OK, "The stream had already started");
            try { await response.Content.ReadAsStringAsync(); }
            // 中转在回传途中中止连接，读到一半断开是预期的；这里只关心乙有没有被试。
            catch (Exception error) when (error is HttpRequestException or IOException) { }
        }
        var report = await Settled(scene.Web, 1); var row = report.Rows.Single(); var tries = report.AttemptsOf(row.RequestId);
        Check(tries.Count == 1 && tries[0].CandidateId == scene.IdA && tries[0].Disposition == "client" && row.Outcome is "interrupted" or "failed", $"{row.Outcome}: {string.Join(" | ", tries)}");
        Check(scene.B.Calls.IsEmpty, "A stream broken midway must never be continued by another upstream");
    }

    /// <summary>
    /// 暂避：甲回 503 被换掉后，5 分钟内的请求直接走乙（甲的计数不再增加，库里只有乙一条）；上游页和概览读出「此刻走的是乙」「甲暂避到几点、原因 503 上游过载」，
    /// 用量页按「第 1 次……换下一家；第 2 次……」读出。注入时钟拨过 5 分钟后回到甲（甲已恢复）。
    /// </summary>
    public static async Task AvoidsThenReturns()
    {
        await using var scene = await Scene.Create();
        scene.A.Handler = context => Fail(context, 503);
        Check(await Call(scene.Web, scene.Token) == HttpStatusCode.OK);
        var failedAt = scene.Clock.GetUtcNow();
        scene.A.Handler = null;
        Check(await Call(scene.Web, scene.Token) == HttpStatusCode.OK && await Call(scene.Web, scene.Token) == HttpStatusCode.OK);
        Check(scene.A.Calls.Count == 1 && scene.B.Calls.Count == 3, $"A={scene.A.Calls.Count}, B={scene.B.Calls.Count}: avoided A must receive nothing");
        var report = await Settled(scene.Web, 3);
        Check(report.Rows.Count(row => report.AttemptsOf(row.RequestId) is [{ CandidateId: var only }] && only == scene.IdB) == 2, "Avoided requests start directly from B");
        await scene.Web.Login();
        var until = (failedAt + TimeSpan.FromMinutes(5)).ToLocalTime().ToString("HH:mm:ss");
        var expected = $"「甲」暂避到 {until}，原因：503，上游过载。";
        foreach (var path in new[] { "/Upstream", "/" })
        {
            var html = WebUtility.HtmlDecode(await scene.Web.Http.GetStringAsync(scene.Web.Url + path));
            Check(html.Contains("此刻走的是「乙」") && html.Contains(expected), $"{path} must read who serves now and why A is avoided");
        }
        var usage = WebUtility.HtmlDecode(await scene.Web.Http.GetStringAsync(scene.Web.Url + "/Usage"));
        Check(usage.Contains("第 1 次：甲，503，上游过载，fixture failure，") && usage.Contains("换下一家；第 2 次：乙，200，"), "Usage page must read both attempts in order");
        scene.Clock.Advance(TimeSpan.FromMinutes(5));
        Check(await Call(scene.Web, scene.Token) == HttpStatusCode.OK && scene.A.Calls.Count == 2 && scene.B.Calls.Count == 3, "After five minutes the active upstream is preferred again");
        var back = await Settled(scene.Web, 4);
        Check(back.AttemptsOf(back.Rows[0].RequestId) is [{ CandidateId: var first }] && first == scene.IdA);
        var page = WebUtility.HtmlDecode(await scene.Web.Http.GetStringAsync(scene.Web.Url + "/Upstream"));
        Check(page.Contains("此刻走的是在用的「甲」。没有上游在暂避。"), "Avoidance ends after five minutes");
    }

    /// <summary>乙的模型清单里没有本次请求的模型：甲回 503 时不试乙，客户端拿到甲的原文，乙收到 0 次。</summary>
    public static async Task SkipsStandbyWithoutModel()
    {
        await using var scene = await Scene.Create();
        scene.Web.Database.Write((connection, transaction) => RelayDatabase.Execute(connection, transaction,
            "UPDATE upstream_candidates SET report_json=json_set(report_json,'$.models',json('[\"other-model\"]')) WHERE id=$id", ("$id", scene.IdB)));
        Check(scene.Web.Upstreams.ReadCandidate(scene.IdB).Report!.Models.SequenceEqual(["other-model"]), "Fixture must change B's model list");
        var body = Body(503, "fixture failure");
        scene.A.Handler = context => Raw(context, 503, body);
        using (var request = Request(scene.Web, scene.Token))
        using (var response = await scene.Web.Http.SendAsync(request))
            Check(response.StatusCode == HttpStatusCode.ServiceUnavailable && await response.Content.ReadAsStringAsync() == body);
        Check(scene.B.Calls.IsEmpty, "A standby without the requested model must not be tried");
        var report = await Settled(scene.Web, 1); Check(report.AttemptsOf(report.Rows.Single().RequestId) is [{ Disposition: "client" }]);
    }

    /// <summary>
    /// 开关关闭（经上游页表单保存）：甲回 503 时客户端收到甲的 503 原文，乙收到 0 次，页面读「自动切换已关闭」；
    /// 再经表单打开，下一次 503 就换到乙。保存即生效，不重启。
    /// </summary>
    public static async Task SwitchOffKeepsToday()
    {
        await using var scene = await Scene.Create();
        await scene.Web.Login();
        async Task<string> Save(bool enabled)
        {
            var values = new Dictionary<string, string> { ["failover"] = enabled ? "true" : "false", ["__RequestVerificationToken"] = await scene.Web.Csrf("/Upstream") };
            using var saved = await scene.Web.Post("/Upstream?handler=Failover", values);
            Check(saved.IsSuccessStatusCode); return WebUtility.HtmlDecode(await saved.Content.ReadAsStringAsync());
        }
        Check((await Save(false)).Contains("自动切换已关闭：只用在用的「甲」") && !scene.Web.Upstreams.ReadOverview().Failover);
        var body = Body(503, "fixture failure");
        scene.A.Handler = context => Raw(context, 503, body);
        using (var request = Request(scene.Web, scene.Token))
        using (var response = await scene.Web.Http.SendAsync(request))
            Check(response.StatusCode == HttpStatusCode.ServiceUnavailable && await response.Content.ReadAsStringAsync() == body, "Switch off: client gets A's 503 verbatim");
        Check(scene.B.Calls.IsEmpty, "Switch off: B must receive nothing");
        var report = await Settled(scene.Web, 1); var row = report.Rows.Single();
        Check(report.AttemptsOf(row.RequestId) is [{ Disposition: "client" } only] && only.CandidateId == scene.IdA && scene.Served(row.RequestId) == scene.IdA);
        Check((await Save(true)).Contains("自动切换已打开") && scene.Web.Upstreams.ReadOverview().Failover);
        Check(await Call(scene.Web, scene.Token) == HttpStatusCode.OK && scene.B.Calls.Count == 1, "Switch on takes effect for the next request");
    }

    /// <summary>
    /// 凭据隔离反例：甲、乙各用虚构凭据，乙收到的请求头里没有甲的凭据，反之亦然；两家都失败、错误说明里各夹着自己的凭据时，
    /// 交给客户端的乙的错误正文、库里的尝试记录、控制台都查不到任何一家的凭据。
    /// </summary>
    public static async Task CredentialsStayHome()
    {
        await using var scene = await Scene.Create();
        scene.A.Handler = context => Fail(context, 503, "bad key " + SecretA);
        scene.B.Handler = context => Fail(context, 502, "bad key " + SecretB);
        string body;
        using (var request = Request(scene.Web, scene.Token))
        using (var response = await scene.Web.Http.SendAsync(request))
        { Check(response.StatusCode == HttpStatusCode.BadGateway, "All failed: the last upstream's status reaches the client"); body = await response.Content.ReadAsStringAsync(); }
        Check(body.Contains("[redacted]") && !body.Contains(SecretA) && !body.Contains(SecretB), body);
        static string Headers(UpstreamFixture upstream) => string.Join('\n', upstream.Calls.SelectMany(call => call.Headers.Select(header => header.Key + ": " + header.Value)));
        Check(Headers(scene.A).Contains(SecretA) && !Headers(scene.A).Contains(SecretB), "A must only receive its own credential");
        Check(Headers(scene.B).Contains(SecretB) && !Headers(scene.B).Contains(SecretA), "B must only receive its own credential");
        var report = await Settled(scene.Web, 1); var tries = report.AttemptsOf(report.Rows.Single().RequestId);
        Check(tries is [{ Disposition: "next", UpstreamStatus: 503 }, { Disposition: "client", UpstreamStatus: 502 }], string.Join(" | ", tries));
        var stored = string.Join('\n', tries.Select(item => item.ToString())) + string.Join('\n', scene.Logs);
        Check(!stored.Contains(SecretA) && !stored.Contains(SecretB), "Attempts and console must not keep any upstream credential");
    }

    /// <summary>
    /// 全部失败：甲回 503、乙回 502（非 JSON），客户端拿到乙的固定错误正文（与没有自动切换时乙在用时逐字相同）；每家只试一次。
    /// </summary>
    public static async Task AllFailHandsOverLast()
    {
        await using var scene = await Scene.Create();
        scene.A.Handler = context => Fail(context, 503);
        scene.B.Handler = async context => { context.Response.StatusCode = 502; context.Response.ContentType = "text/html"; await context.Response.WriteAsync("<html>Bad Gateway</html>"); };
        using (var request = Request(scene.Web, scene.Token))
        using (var response = await scene.Web.Http.SendAsync(request))
            Check(response.StatusCode == HttpStatusCode.BadGateway && await response.Content.ReadAsStringAsync()
                == """{"type":"error","error":{"type":"api_error","message":"Upstream returned an unreadable error response."}}""");
        Check(scene.A.Calls.Count == 1 && scene.B.Calls.Count == 1, "Each upstream is tried at most once per request");
        var report = await Settled(scene.Web, 1);
        Check(report.AttemptsOf(report.Rows.Single().RequestId) is [{ Disposition: "next" }, { Disposition: "client", ErrorType: "unreadable" }]);
    }

    /// <summary>用量归属与计价：甲、乙各有不同单价，甲回 503 换到乙，金额按乙的单价估算（10 输入、20 输出 → 0.00005 USD），用量记乙的主键。</summary>
    public static async Task PricesTheServingUpstream()
    {
        await using var scene = await Scene.Create();
        scene.Web.Pricing.Save(scene.IdA, "fixture-model", "USD", 100, 200, 0, 0);
        scene.Web.Pricing.Save(scene.IdB, "fixture-model", "USD", 1, 2, 0, 0);
        scene.A.Handler = context => Fail(context, 503);
        scene.B.Handler = context => Json(context, new { input_tokens = 10, output_tokens = 20 });
        Check(await Call(scene.Web, scene.Token) == HttpStatusCode.OK);
        var row = (await Settled(scene.Web, 1)).Rows.Single();
        Check(row.Money == new UsageMoney(0.00005m, "USD", "estimated", "complete") && scene.Served(row.RequestId) == scene.IdB, $"{row.Money}");
    }

    /// <summary>
    /// 架构升级 v5 → v6：失败整体回滚（版本号仍为 5、没有开关列、旧单价原样）；去掉故障后升级成功：开关默认开，属于当前活动版本的旧单价改挂在用那一家，
    /// 已有用量按尝试记录回填实际服务的那一家，一条不少。
    /// </summary>
    public static async Task SchemaUpgrade()
    {
        using var home = new TestHome(); var process = new TestProcess(home.Path); var token = process.Identities.Create("升级");
        var user = process.Identities.Clients.Authenticate(token.Token)!;
        await process.Usage.Start("kept", user, "inference"); await process.Usage.Dispatch("kept", 1, null, "fixture-model");
        await process.Usage.Finish("kept", new UsageEnd("succeeded", true, 200, 200, 1, "complete", new(3, 4)) { Attempts = [new UpstreamAttempt(1, "served-upstream", 1, DateTimeOffset.UtcNow)] });
        process.Database.Write((connection, transaction) => RelayDatabase.Execute(connection, transaction, """
            UPDATE upstream SET candidate_id='active-upstream';
            ALTER TABLE upstream DROP COLUMN failover;
            ALTER TABLE usage_requests DROP COLUMN candidate_id;
            DROP TABLE usage_prices;
            CREATE TABLE usage_prices(id TEXT PRIMARY KEY,upstream_version INTEGER NOT NULL,model TEXT NOT NULL,json TEXT NOT NULL,UNIQUE(upstream_version,model));
            INSERT INTO usage_prices VALUES('current',(SELECT version FROM upstream WHERE id=1),'fixture-model','{"Id":"current","UpstreamVersion":1,"Model":"fixture-model","Currency":"USD","InputPerMillion":1}');
            INSERT INTO usage_prices VALUES('older',-1,'fixture-model','{"Id":"older","UpstreamVersion":-1,"Model":"fixture-model","Currency":"USD","InputPerMillion":9}');
            PRAGMA user_version=5;
            """));
        var rejected = false;
        try { _ = new TestProcess(home.Path, migrationCheckpoint: () => throw new IOException("fixture-checkpoint")).Database; }
        catch (IOException error) when (error.Message == "fixture-checkpoint") { /* 检查点在改表之后、写版本号之前失败，整个升级应回滚；随后核对版本号、列和单价。 */ rejected = true; }
        Check(rejected, "Checkpoint failure must abort the upgrade");
        int Count(RelayDatabase database, string sql) { using var connection = database.Open(); return Convert.ToInt32(RelayDatabase.Scalar(connection, null, sql)); }
        Check(Count(process.Database, "PRAGMA user_version") == 5 && Count(process.Database, "SELECT COUNT(*) FROM pragma_table_info('upstream') WHERE name='failover'") == 0);
        Check(Count(process.Database, "SELECT COUNT(*) FROM usage_prices WHERE upstream_version IS NOT NULL") == 2, "Old prices stay untouched after rollback");
        var upgraded = new TestProcess(home.Path);
        Check(Count(upgraded.Database, "PRAGMA user_version") == 6 && Count(upgraded.Database, "SELECT failover FROM upstream WHERE id=1") == 1);
        var prices = upgraded.Pricing.List("active-upstream");
        Check(prices is [{ Id: "current", CandidateId: "active-upstream", InputPerMillion: 1 }] && Count(upgraded.Database, "SELECT COUNT(*) FROM usage_prices") == 1, "Current-version prices move to the active upstream");
        using (var connection = upgraded.Database.Open())
            Check((string?)RelayDatabase.Scalar(connection, null, "SELECT candidate_id FROM usage_requests WHERE request_id='kept'") == "served-upstream");
        Check(upgraded.Usage.Query(new()).Rows.Single().Tokens == new UsageTokens(3, 4));
    }

    /// <summary>甲某个状态码对应的 Anthropic 错误类型，与 <see cref="Fail"/> 写出的一致。</summary>
    private static string ErrorType(int status) => status switch
    {
        400 => "invalid_request_error", 401 => "authentication_error", 404 => "not_found_error", 429 => "rate_limit_error",
        502 => "api_error", _ => "overloaded_error"
    };

    /// <summary>按状态码写出 Anthropic 格式的错误正文原文。</summary>
    private static string Body(int status, string message) => "{\"type\":\"error\",\"error\":{\"type\":\"" + ErrorType(status) + "\",\"message\":\"" + message + "\"}}";

    /// <summary>写出 Anthropic 格式的错误响应；429 带 Retry-After。</summary>
    private static Task Fail(HttpContext context, int status, string message = "fixture failure")
    {
        if (status == 429) context.Response.Headers.RetryAfter = "30";
        return Raw(context, status, Body(status, message));
    }

    /// <summary>按给定状态码和 JSON 原文应答。</summary>
    private static async Task Raw(HttpContext context, int status, string body)
    { context.Response.StatusCode = status; context.Response.ContentType = "application/json"; await context.Response.WriteAsync(body); }

    /// <summary>
    /// 一个用例的场景：两家模拟上游、注入时钟的管理网页主机（事件日志收进队列）、一个使用者；甲、乙都保存并测试通过，甲在用，乙按上游页顺序排在后面。
    /// 搭场景时的测试调用不计入两家的计数。
    /// </summary>
    private sealed class Scene : IAsyncDisposable
    {
        /// <summary>模拟上游甲：在用的那一家，名称「甲」，凭据 <see cref="SecretA"/>。</summary>
        public required UpstreamFixture A { get; init; }
        /// <summary>模拟上游乙：测试通过的备用，名称「乙」，凭据 <see cref="SecretB"/>。</summary>
        public required UpstreamFixture B { get; init; }
        /// <summary>注入了 <see cref="Clock"/> 与事件日志队列的管理网页主机。</summary>
        public required WebFixture Web { get; init; }
        /// <summary>暂避计时与用量时间共用的可拨动时钟。</summary>
        public required UsageClock Clock { get; init; }
        /// <summary>控制台事件行，按写入顺序。</summary>
        public required ConcurrentQueue<string> Logs { get; init; }
        /// <summary>使用者的个人令牌。</summary>
        public required string Token { get; init; }
        /// <summary>甲的已保存上游主键。</summary>
        public required string IdA { get; init; }
        /// <summary>乙的已保存上游主键。</summary>
        public required string IdB { get; init; }

        /// <summary>搭好场景：甲、乙各自保存并测试通过，激活甲，清空两家计数。</summary>
        public static async Task<Scene> Create()
        {
            var a = await UpstreamFixture.Create(); var b = await UpstreamFixture.Create();
            var clock = new UsageClock(DateTimeOffset.UtcNow); var logs = new ConcurrentQueue<string>();
            var web = await WebFixture.Create(overrides: services => { services.AddSingleton<TimeProvider>(clock); services.AddSingleton<IRelayEventLog>(new FixtureLog(logs)); });
            var token = web.Identities.Create("切换").Token;
            var idA = await Passed(web, a, SecretA, "甲"); var idB = await Passed(web, b, SecretB, "乙");
            web.Upstreams.Activate(idA); a.Calls.Clear(); b.Calls.Clear();
            return new() { A = a, B = b, Web = web, Clock = clock, Logs = logs, Token = token, IdA = idA, IdB = idB };
        }

        /// <summary>模拟「测过之后变坏」：保存一个指向 <paramref name="options"/> 的候选，抄甲的通过报告后切换过去，返回它的主键；甲、乙都成了备用。</summary>
        public string ForceActive(UpstreamOptions options, string name)
        {
            var id = Web.Upstreams.SaveCandidate(Web.Upstreams.ReadActive().Version, options, SecretA, name);
            Web.Database.Write((connection, transaction) => RelayDatabase.Execute(connection, transaction, """
                UPDATE upstream_candidates SET state='passed',report_json=(SELECT report_json FROM upstream WHERE id=1) WHERE id=$id
                """, ("$id", id)));
            Web.Upstreams.Activate(id); return id;
        }

        /// <summary>用量记录里实际服务那一家的主键。</summary>
        public string? Served(string requestId)
        {
            using var connection = Web.Database.Open();
            return RelayDatabase.Scalar(connection, null, "SELECT candidate_id FROM usage_requests WHERE request_id=$id", ("$id", requestId)) as string;
        }

        /// <summary>取出中转请求的事件行（带 requestId 与 route 的那种），按写入顺序。</summary>
        public List<JsonElement> Events() =>
            Logs.Select(line => JsonDocument.Parse(line).RootElement).Where(item => item.TryGetProperty("route", out _)).ToList();

        /// <summary>释放网页主机和两家模拟上游。</summary>
        public async ValueTask DisposeAsync() { await Web.DisposeAsync(); await A.DisposeAsync(); await B.DisposeAsync(); }

        /// <summary>以给定名称和凭据保存指向模拟上游的候选并测试通过，返回主键。</summary>
        private static async Task<string> Passed(WebFixture web, UpstreamFixture upstream, string credential, string name)
        {
            var id = web.Upstreams.SaveCandidate(web.Upstreams.ReadActive().Version, upstream.Options(), credential, name);
            var report = await web.Upstreams.TestCandidate(id, true, CancellationToken.None); Check(report.CanActivate, report.Message); return id;
        }
    }
}
