using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using static Tests.TestSuite;
using static Tests.UsageFixture;

namespace Tests;

/// <summary>每次上游尝试的详细记录：库里的错误类型与分类、控制台事件行、用量页读法，以及返回给客户端的内容与改动前逐字相同。</summary>
static class UpstreamAttemptTests
{
    private const string Secret = "fixture-new-upstream-secret";

    /// <summary>
    /// 先成功一次、再让上游回 Anthropic 格式的 503 overloaded_error：客户端拿到的状态、内容类型、请求编号头与正文和上游原文逐字相同；
    /// 库里两次尝试都指向候选主键，失败那次记 overloaded_error、Overloaded、上游请求编号与到响应头耗时；控制台事件行在原字段后补上游名称、尝试次数、错误类型；
    /// 用量页按「哪家，状态码，错误类型，说明，耗时」读出；个人用量 JSON 不带尝试明细。
    /// </summary>
    public static async Task Overloaded503()
    {
        var (upstream, web, logs) = await Start(); await using var _ = upstream; await using var __ = web;
        var user = web.Identities.Create("尝试记录"); var candidate = await Activate(web, upstream, "顺丰中转");
        Check(await Call(web, user.Token) == HttpStatusCode.OK);
        const string body = """{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}""";
        upstream.Handler = async context =>
        {
            context.Response.StatusCode = 503; context.Response.Headers["request-id"] = "req_fixture_503"; context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(body);
        };
        using (var request = Request(web, user.Token))
        using (var response = await web.Http.SendAsync(request))
        {
            Check(response.StatusCode == HttpStatusCode.ServiceUnavailable && await response.Content.ReadAsStringAsync() == body, "503 body must reach the client verbatim");
            Check(response.Content.Headers.ContentType?.ToString() == "application/json; charset=utf-8" && response.Headers.GetValues("request-id").Single() == "req_fixture_503");
        }
        var report = await Settled(web, 2); var success = report.AttemptsOf(report.Rows.Single(row => row.Outcome == "succeeded").RequestId).Single();
        var failure = report.AttemptsOf(report.Rows.Single(row => row.Outcome == "failed").RequestId).Single();
        Check(success.CandidateId == candidate && success.Name == "顺丰中转" && success.UpstreamStatus == 200 && success.ErrorType is null && success.HeadersMs is not null);
        Check(failure.CandidateId == candidate && failure.Number == 1 && failure.UpstreamStatus == 503 && failure.ErrorType == "overloaded_error"
            && failure.ErrorMessage == "Overloaded" && failure.ConnectFailure is null && failure.Disposition == "client" && failure.UpstreamRequestId == "req_fixture_503"
            && failure.Version == web.Upstreams.ReadActive().Version && failure.HeadersMs <= failure.DurationMs, "503 attempt fields");
        var events = Events(logs);
        Check(events.Last().EnumerateObject().Select(item => item.Name).SequenceEqual(["time", "requestId", "client", "route", "status", "outcome", "durationMs", "upstream", "attempts", "error"]),
            "Event line keeps its original fields in order and appends upstream, attempts and error");
        Check(events.Last().GetProperty("upstream").GetString() == "顺丰中转" && events.Last().GetProperty("attempts").GetInt32() == 1 && events.Last().GetProperty("error").GetString() == "overloaded_error");
        Check(events.Any(item => item.GetProperty("status").GetInt32() == 200 && item.GetProperty("attempts").GetInt32() == 1 && item.GetProperty("error").ValueKind == JsonValueKind.Null));
        var page = await UsagePage(web);
        Check(page.Contains("顺丰中转，503，上游过载，Overloaded，") && page.Contains("顺丰中转，200，"), "Usage page must read the upstream, status, error and duration");
        using var personal = new HttpRequestMessage(HttpMethod.Get, web.Url + "/my-usage/data"); personal.Headers.Authorization = new("Bearer", user.Token);
        using var mine = await web.Http.SendAsync(personal); var json = await mine.Content.ReadAsStringAsync();
        Check(mine.IsSuccessStatusCode && !json.Contains("attempts", StringComparison.OrdinalIgnoreCase) && !json.Contains("overloaded_error"), "Personal usage must not expose upstream diagnostics");
    }

    /// <summary>上游回非 JSON 的 502：客户端拿到与改动前相同的固定错误正文；库里错误类型记 unreadable、没有说明、保留状态码 502；页面读「错误正文无法解析」。</summary>
    public static async Task Unreadable502()
    {
        var (upstream, web, logs) = await Start(); await using var _ = upstream; await using var __ = web;
        var user = web.Identities.Create("非 JSON"); await Activate(web, upstream, "顺丰中转");
        upstream.Handler = async context => { context.Response.StatusCode = 502; context.Response.ContentType = "text/html"; await context.Response.WriteAsync("<html>Bad Gateway " + Secret + "</html>"); };
        using (var request = Request(web, user.Token))
        using (var response = await web.Http.SendAsync(request))
            Check(response.StatusCode == HttpStatusCode.BadGateway && await response.Content.ReadAsStringAsync()
                == """{"type":"error","error":{"type":"api_error","message":"Upstream returned an unreadable error response."}}""", "Unreadable 502 body unchanged");
        var attempt = Only(await Settled(web, 1));
        Check(attempt.UpstreamStatus == 502 && attempt.ErrorType == "unreadable" && attempt.ErrorMessage is null && attempt.ConnectFailure is null);
        Check(Events(logs).Last().GetProperty("error").GetString() == "unreadable");
        Check((await UsagePage(web)).Contains("顺丰中转，502，错误正文无法解析，"));
    }

    /// <summary>
    /// 建连超时：上游端口接受 TCP 却从不回 TLS 握手，15 秒建连超时后客户端拿到与改动前相同的 504 固定正文；
    /// 库里记 connection_failed / connect_timeout、没有上游状态码，页面读「连不上上游：建连超时」。
    /// </summary>
    public static async Task ConnectTimeout()
    {
        var (upstream, web, logs) = await Start(); await using var _ = upstream; await using var __ = web;
        var user = web.Identities.Create("建连超时"); await Activate(web, upstream, "顺丰中转");
        using var silent = new TcpListener(IPAddress.Loopback, 0); silent.Start();
        await ForceActive(web, upstream.Options() with { BaseUrl = "https://127.0.0.1:" + ((IPEndPoint)silent.LocalEndpoint).Port }, "沉默上游");
        using (var request = Request(web, user.Token))
        using (var response = await web.Http.SendAsync(request))
            Check(response.StatusCode == HttpStatusCode.GatewayTimeout && await response.Content.ReadAsStringAsync()
                == """{"type":"error","error":{"type":"api_error","message":"Upstream request timed out."}}""", "Connect timeout body unchanged");
        var attempt = Only(await Settled(web, 1));
        Check(attempt.ErrorType == "connection_failed" && attempt.ConnectFailure == "connect_timeout" && attempt.UpstreamStatus is null && attempt.HeadersMs is null
            && attempt.DurationMs >= 14000, $"Connect timeout attempt: {attempt}");
        Check(Events(logs).Last().GetProperty("upstream").GetString() == "沉默上游");
        Check((await UsagePage(web)).Contains("沉默上游，无响应，连不上上游：建连超时，"));
    }

    /// <summary>
    /// TLS 失败：上游证书不受信任，凭据发出前就失败，客户端拿到与改动前相同的 502 固定正文，库里记 connection_failed / tls；
    /// 再换成没人监听的端口，同样的 502 固定正文，库里记 refused（拨号器保留了底层套接字错误）。
    /// </summary>
    public static async Task TlsFailure()
    {
        var (upstream, web, _) = await Start(); await using var __ = upstream; await using var ___ = web;
        var user = web.Identities.Create("TLS 失败"); await Activate(web, upstream, "顺丰中转");
        using var generated = TestProcess.CertificateFactory.CreateCertificate("localhost", out _);
        using var certificate = TestProcess.CertificateFactory.LoadServerCertificate(generated.Export(X509ContentType.Pfx));
        await using var untrusted = await UpstreamFixture.Create(certificate);
        await ForceActive(web, untrusted.Options(), "证书不受信");
        const string failed = """{"type":"error","error":{"type":"api_error","message":"Upstream connection failed. Check host connectivity."}}""";
        using (var request = Request(web, user.Token))
        using (var response = await web.Http.SendAsync(request))
            Check(response.StatusCode == HttpStatusCode.BadGateway && await response.Content.ReadAsStringAsync() == failed && untrusted.Calls.IsEmpty, "TLS failure body unchanged");
        var tls = Only(await Settled(web, 1));
        Check(tls.ErrorType == "connection_failed" && tls.ConnectFailure == "tls" && tls.UpstreamStatus is null, $"TLS attempt: {tls}");
        var closed = new TcpListener(IPAddress.Loopback, 0); closed.Start(); var port = ((IPEndPoint)closed.LocalEndpoint).Port; closed.Stop();
        await ForceActive(web, upstream.Options() with { BaseUrl = "http://127.0.0.1:" + port }, "没人监听");
        using (var request = Request(web, user.Token))
        using (var response = await web.Http.SendAsync(request))
            Check(response.StatusCode == HttpStatusCode.BadGateway && await response.Content.ReadAsStringAsync() == failed, "Refused body unchanged");
        var refused = (await Settled(web, 2)).Attempts.Values.Select(list => list.Single()).Single(item => item.Name == "没人监听");
        Check(refused.ErrorType == "connection_failed" && refused.ConnectFailure == "refused", $"Refused attempt: {refused}");
        Check((await UsagePage(web)).Contains("证书不受信，无响应，连不上上游：TLS 握手失败，"));
    }

    /// <summary>
    /// 秘密反例：上游错误说明里夹着上游凭据和个人令牌，库里尝试记录、控制台事件行、用量页三处都查不到它们和管理密码，只剩 [redacted]；
    /// 说明超过 300 字时截到 300 字以内并以省略号结尾。
    /// </summary>
    public static async Task SecretsNeverRecorded()
    {
        var (upstream, web, logs) = await Start(); await using var _ = upstream; await using var __ = web;
        var user = web.Identities.Create("秘密反例"); await Activate(web, upstream, "顺丰中转");
        var message = "invalid key " + Secret + " for " + user.Token + " " + new string('x', 400);
        upstream.Handler = context => { context.Response.StatusCode = 500; return context.Response.WriteAsJsonAsync(new { type = "error", error = new { type = "api_error", message } }); };
        Check(await Call(web, user.Token) == HttpStatusCode.InternalServerError);
        var attempt = Only(await Settled(web, 1));
        Check(attempt.ErrorMessage!.Length <= UpstreamAttempt.MessageLimit && attempt.ErrorMessage.EndsWith('…') && attempt.ErrorMessage.Contains("[redacted]"), attempt.ErrorMessage);
        string stored;
        using (var connection = web.Database.Open())
            stored = (string)RelayDatabase.Scalar(connection, null, """
                SELECT group_concat(coalesce(candidate_id,'')||coalesce(upstream_request_id,'')||coalesce(error_type,'')||coalesce(error_message,'')||coalesce(connect_failure,''),'|') FROM usage_attempts
                """)!;
        var page = await UsagePage(web); var console = string.Join('\n', logs);
        foreach (var (place, text) in new[] { ("database", stored), ("console", console), ("page", page) })
            foreach (var secret in new[] { Secret, user.Token, WebFixture.Password })
                Check(!text.Contains(secret, StringComparison.Ordinal), $"{place} leaked a secret");
        Check(stored.Contains("[redacted]") && page.Contains("[redacted]"));
    }

    /// <summary>40 天清理：固定时钟下，早于窗口一个刻度的用量连同它的尝试记录一起删掉，恰好在边界上的保留。</summary>
    public static async Task AttemptsExpireWithUsage()
    {
        using var home = new TestHome(); var process = new TestProcess(home.Path); var token = process.Identities.Create("清理");
        var user = process.Identities.Clients.Authenticate(token.Token)!;
        var now = new DateTimeOffset(2026, 11, 2, 6, 30, 0, TimeSpan.Zero); var clock = new UsageClock(now); var usage = new TestProcess(home.Path, clock).Usage;
        foreach (var (id, time) in new[] { ("expired", now.AddDays(-40).AddTicks(-1)), ("boundary", now.AddDays(-40)) })
        {
            clock.Set(time); await usage.Start(id, user, "inference"); await usage.Dispatch(id, 1, "fixture-model");
            Check(await usage.Finish(id, new UsageEnd("failed", true, 503, 503, 1, "partial", new()) { Attempts = [new UpstreamAttempt(1, "fixture-candidate", 1, time)] }));
        }
        int Count(string sql) { using var connection = process.Database.Open(); return Convert.ToInt32(RelayDatabase.Scalar(connection, null, sql)); }
        Check(Count("SELECT COUNT(*) FROM usage_attempts") == 2);
        clock.Set(now); Check(await usage.Cleanup() == 1);
        Check(Count("SELECT COUNT(*) FROM usage_attempts WHERE request_id='expired'") == 0 && Count("SELECT COUNT(*) FROM usage_attempts WHERE request_id='boundary'") == 1);
        Check(Count("SELECT COUNT(*) FROM usage_requests") == 1);
    }

    /// <summary>架构升级 v4 → v5 失败整体回滚：版本号仍为 4、没有尝试表、已有用量一条不少；去掉故障后升级成功，用量原样可查。</summary>
    public static async Task SchemaRollback()
    {
        using var home = new TestHome(); var process = new TestProcess(home.Path); var token = process.Identities.Create("迁移");
        var user = process.Identities.Clients.Authenticate(token.Token)!;
        await process.Usage.Start("kept", user, "inference"); await process.Usage.Finish("kept", new UsageEnd("succeeded", true, 200, 200, 1, "complete", new(3, 4)));
        process.Database.Write((connection, transaction) => RelayDatabase.Execute(connection, transaction, "DROP TABLE usage_attempts; PRAGMA user_version=4;"));
        var rejected = false;
        try { _ = new TestProcess(home.Path, migrationCheckpoint: () => throw new IOException("fixture-checkpoint")).Database; }
        catch (IOException error) when (error.Message == "fixture-checkpoint") { /* 检查点在建表之后、写版本号之前失败，整个升级应回滚；随后核对版本号、表和用量。 */ rejected = true; }
        Check(rejected, "Checkpoint failure must abort the upgrade");
        using (var connection = process.Database.Open())
        {
            Check(Convert.ToInt32(RelayDatabase.Scalar(connection, null, "PRAGMA user_version")) == 4);
            Check(Convert.ToInt32(RelayDatabase.Scalar(connection, null, "SELECT COUNT(*) FROM sqlite_master WHERE name='usage_attempts'")) == 0);
            Check(Convert.ToInt32(RelayDatabase.Scalar(connection, null, "SELECT COUNT(*) FROM usage_requests WHERE request_id='kept' AND input_tokens=3")) == 1);
        }
        var upgraded = new TestProcess(home.Path);
        using (var connection = upgraded.Database.Open()) Check(Convert.ToInt32(RelayDatabase.Scalar(connection, null, "PRAGMA user_version")) == 5);
        Check(upgraded.Usage.Query(new()).Rows.Single().Tokens == new UsageTokens(3, 4));
    }

    /// <summary>连接失败分类直接按异常链判定：域名解析、拒绝连接、代理不可用、TLS、建连超时、中转总超时、客户端取消、回传中断与中转自己的固定提示。</summary>
    public static Task Classification()
    {
        static UpstreamAttempt Fail(Exception error, bool aborted = false)
        { var attempt = new UpstreamAttempt(1, null, 1, DateTimeOffset.UtcNow); attempt.Failed(error, aborted, value => value); return attempt; }
        var cases = new (Exception Error, bool Aborted, string Type, string? Connect)[]
        {
            (new HttpRequestException("x", new SocketException((int)SocketError.HostNotFound)), false, "connection_failed", "dns"),
            (new HttpRequestException("x", new HttpRequestException("y", new SocketException((int)SocketError.ConnectionRefused))), false, "connection_failed", "refused"),
            (new HttpRequestException("x", new UpstreamProxyUnavailableException("proxy")), false, "connection_failed", "proxy"),
            (new HttpRequestException(HttpRequestError.SecureConnectionError, "x"), false, "connection_failed", "tls"),
            (new HttpRequestException("x", new System.Security.Authentication.AuthenticationException("y")), false, "connection_failed", "tls"),
            (new TaskCanceledException("x", new TimeoutException()), false, "connection_failed", "connect_timeout"),
            (new HttpRequestException("x"), false, "connection_failed", "other"),
            (new OperationCanceledException(), false, "timeout", null),
            (new OperationCanceledException(), true, "cancelled", null),
            (new IOException("x"), false, "interrupted", null),
            (new InvalidOperationException("x"), false, "relay_error", null),
        };
        foreach (var (error, aborted, type, connect) in cases)
        {
            var attempt = Fail(error, aborted);
            Check(attempt.ErrorType == type && attempt.ConnectFailure == connect && attempt.ErrorMessage is null, $"{error.GetType().Name} → {attempt.ErrorType}/{attempt.ConnectFailure}");
        }
        var redirect = Fail(new RelayFailure(502, "Upstream redirect rejected."));
        Check(redirect.ErrorType == "protocol" && redirect.ErrorMessage == "Upstream redirect rejected.");
        var row = new UpstreamAttemptRow(1, "gone", null, 3, DateTimeOffset.UtcNow, null, 15240, null, null, "connection_failed", null, "connect_timeout", "client");
        Check(UsageLabels.Attempts([row]) == "已删除的上游，无响应，连不上上游：建连超时，15.2 秒" && UsageLabels.Attempts([]) == "未发往上游");
        return Task.CompletedTask;
    }

    /// <summary>起一个模拟上游和一个把事件日志收进队列的管理网页主机。</summary>
    private static async Task<(UpstreamFixture Upstream, WebFixture Web, ConcurrentQueue<string> Logs)> Start()
    {
        var logs = new ConcurrentQueue<string>(); var upstream = await UpstreamFixture.Create();
        var web = await WebFixture.Create(overrides: services => services.AddSingleton<IRelayEventLog>(new FixtureLog(logs)));
        return (upstream, web, logs);
    }

    /// <summary>以给定供应商名称保存、测试并激活指向模拟上游的候选，返回候选主键。</summary>
    private static async Task<string> Activate(WebFixture web, UpstreamFixture upstream, string name)
    {
        var id = web.Upstreams.SaveCandidate(web.Upstreams.ReadActive().Version, upstream.Options(), Secret, name);
        Check((await web.Upstreams.TestCandidate(id, true, CancellationToken.None)).CanActivate); web.Upstreams.Activate(id); return id;
    }

    /// <summary>模拟「测过之后上游变坏」：保存一个连不上的候选，把当前在用那家的通过报告抄给它，再切换过去，返回它的主键。</summary>
    private static Task<string> ForceActive(WebFixture web, UpstreamOptions options, string name)
    {
        var active = web.Upstreams.ReadActive(); var id = web.Upstreams.SaveCandidate(active.Version, options, Secret, name);
        web.Database.Write((connection, transaction) => RelayDatabase.Execute(connection, transaction, """
            UPDATE upstream_candidates SET state='passed',report_json=(SELECT report_json FROM upstream WHERE id=1) WHERE id=$id
            """, ("$id", id)));
        web.Upstreams.Activate(id); return Task.FromResult(id);
    }

    /// <summary>报告里只有一条明细时取它唯一的一次上游尝试。</summary>
    private static UpstreamAttemptRow Only(UsageReport report) => report.AttemptsOf(report.Rows.Single().RequestId).Single();

    /// <summary>登录后打开管理员用量页，返回解码 HTML 实体后的正文。</summary>
    private static async Task<string> UsagePage(WebFixture web)
    { await web.Login(); return WebUtility.HtmlDecode(await web.Http.GetStringAsync(web.Url + "/Usage")); }

    /// <summary>取出中转请求的事件行（带 requestId 与 route 的那种），按写入顺序。</summary>
    private static List<JsonElement> Events(ConcurrentQueue<string> logs) =>
        logs.Select(line => JsonDocument.Parse(line).RootElement).Where(item => item.TryGetProperty("route", out _)).ToList();
}
