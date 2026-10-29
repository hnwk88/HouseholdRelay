using System.Net;
using System.Text;
using System.Text.Json;
using HouseholdRelay;
using Microsoft.AspNetCore.Http;
using static Tests.TestSuite;

namespace Tests;

/// <summary>复现仅接受原生请求特征的上游，并验证显式检测、激活和客户端转发。</summary>
static class ClaudeCodeProbeTests
{
    private const string Events = """
        event: message_start
        data: {"type":"message_start","message":{"id":"msg_probe","type":"message","role":"assistant","model":"claude-opus-5-5","content":[],"usage":{"input_tokens":1,"output_tokens":1}}}

        event: ping
        data: {"type":"ping"}

        event: content_block_start
        data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"OK"}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":0}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":1}}

        event: message_stop
        data: {"type":"message_stop"}


        """;

    /// <summary>
    /// 验证只接受原生 Claude Code 请求特征的受限上游：普通诊断认证通过但因 400 不可激活，且只调用一次、不重放也不偷偷改用原生诊断；
    /// 显式选择原生诊断后可激活，诊断请求走带前缀的路径、只带 Bearer 宿主密钥、不带 oauth beta、不启用工具；
    /// 成员用原生请求体经中转调用时请求体逐字节保留、只用宿主密钥、成员令牌不外泄；诊断方式重启后仍保留；
    /// 之后原生诊断也被拒时候选不可激活、报告不含密钥、只调用一次，当前版本不变也不被暂停。
    /// </summary>
    public static async Task RestrictedUpstream()
    {
        await using var mock = await UpstreamFixture.Create(); await using var web = await WebFixture.Create();
        var service = web.Upstreams;
        mock.Handler = async context =>
        {
            if (context.Request.Path.Value!.EndsWith("/models", StringComparison.Ordinal)) { await UpstreamFixture.Success(context); return; }
            var call = mock.Calls.Last(); using var body = JsonDocument.Parse(call.Body);
            if (!call.Headers["User-Agent"].StartsWith("claude-cli/", StringComparison.Ordinal) || !body.RootElement.TryGetProperty("system", out _))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsJsonAsync(new { error = new { type = "client_invalid_request", code = "client_request_rejected" } }); return;
            }
            context.Response.ContentType = "text/event-stream";
            foreach (var frame in Events.Split("\n\n")) { await context.Response.WriteAsync(frame + "\n\n"); await context.Response.Body.FlushAsync(); }
        };
        var options = mock.Options("/v1/cc", authentication: "bearer") with { Model = "claude-opus-5-5" };
        var standard = service.SaveCandidate(service.ReadActive().Version, options, "fixture-probe-secret");
        var failed = await service.TestCandidate(standard, true, CancellationToken.None);
        Check(failed.Authentication == "passed" && !failed.CanActivate && failed.Message.Contains("400"));
        Check(mock.Calls.Count(call => call.Path.Contains("/messages")) == 1, "Rejected generation must not replay or silently switch diagnostics");
        var id = service.SaveCandidate(service.ReadActive().Version, options with { Diagnostic = "claude-code" }, "fixture-probe-secret");
        var report = await service.TestCandidate(id, true, CancellationToken.None);
        Check(report.CanActivate, report.Message); service.Activate(id);
        var native = mock.Calls.Last(); Check(native.Path == "/v1/cc/v1/messages?beta=true");
        Check(native.Headers["Authorization"] == "Bearer fixture-probe-secret" && !native.Headers.ContainsKey("x-api-key"));
        Check(!native.Headers.GetValueOrDefault("anthropic-beta", "").Contains("oauth-"));
        using var nativeBody = JsonDocument.Parse(native.Body);
        Check(nativeBody.RootElement.GetProperty("tools").GetArrayLength() == 0, "Diagnostic must not enable tools");
        var person = web.Identities.Create("原生客户端");
        using var request = new HttpRequestMessage(HttpMethod.Post, web.Url + "/v1/messages?beta=true")
        { Content = new StringContent(native.Body, Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new("Bearer", person.Token); request.Headers.Add("User-Agent", native.Headers["User-Agent"]);
        using var relayed = await web.Http.SendAsync(request); Check(relayed.StatusCode == HttpStatusCode.OK);
        Check((await relayed.Content.ReadAsStringAsync()).Contains("message_stop"));
        Check(mock.Calls.Last().Headers["Authorization"] == "Bearer fixture-probe-secret");
        Check(mock.Calls.Last().Body == native.Body && mock.Calls.All(call => call.Headers.Values.All(value => !value.Contains(person.Token))));
        var persisted = new TestProcess(web.Home.Path).Upstreams.ReadActive();
        Check(persisted.Options.Diagnostic == "claude-code" && persisted.Report?.CanActivate == true);
        var version = persisted.Version; var before = mock.Calls.Count;
        mock.Handler = async context =>
        {
            if (context.Request.Path.Value!.EndsWith("/models", StringComparison.Ordinal)) { await UpstreamFixture.Success(context); return; }
            context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = new { type = "invalid_request_error", message = "fixture-probe-secret" } });
        };
        var rejected = service.SaveCandidate(version, options with { Diagnostic = "claude-code" }, "fixture-probe-secret");
        var rejectedReport = await service.TestCandidate(rejected, true, CancellationToken.None);
        Check(!rejectedReport.CanActivate && !JsonSerializer.Serialize(rejectedReport).Contains("fixture-probe-secret"));
        Check(mock.Calls.Skip(before).Count(call => call.Path.Contains("/messages")) == 1);
        Check(service.ReadActive().Version == version && !web.Relay.Upstreams.Status(version).Paused);
    }

    /// <summary>
    /// 验证原生诊断被取消时释放客户端和上游：上游挂起不应答，诊断开始后取消，报告为已取消且不可激活，当前版本不变；
    /// 模拟上游必须在 3 秒内收到中止，且消息接口只被调用一次。
    /// </summary>
    public static async Task Cancellation()
    {
        await using var mock = await UpstreamFixture.Create(); await using var web = await WebFixture.Create();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        mock.Handler = async context =>
        {
            if (context.Request.Path.Value!.EndsWith("/models", StringComparison.Ordinal)) { await UpstreamFixture.Success(context); return; }
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted); }
            // 诊断取消后必须中止上游请求；捕获到取消即证明取消已传播到模拟上游。
            catch (OperationCanceledException) { released.TrySetResult(); }
        };
        var service = web.Upstreams; var version = service.ReadActive().Version;
        var id = service.SaveCandidate(version, mock.Options() with { Diagnostic = "claude-code", Model = "claude-opus-5-5" }, "fixture-cancel-secret");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var pending = service.TestCandidate(id, true, cancellation.Token);
        // 确认诊断请求已到达上游后再取消，保证测的是在途取消而不是尚未发出的请求。
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20)); cancellation.Cancel();
        var report = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        await released.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Check(report.Call == "cancelled" && !report.CanActivate && service.ReadActive().Version == version, report.Call + ": " + report.Message);
        Check(mock.Calls.Count(call => call.Path.Contains("/messages")) == 1);
    }

    /// <summary>
    /// 验证原生诊断的流校验只接受完整、正常结束的消息流：完整流（LF 或 CRLF 换行）返回模型名；
    /// 只有 message_stop、缺少 message_stop、文本为空、末尾追加错误事件、模型名等于凭据、结束原因为空时一律视为无效。
    /// </summary>
    public static Task StreamValidation()
    {
        string? Read(string value) => ProbeMessage.Read(Encoding.UTF8.GetBytes(value), true, "fixture-secret");
        Check(Read(Events) == "claude-opus-5-5");
        Check(Read(Events.Replace("\n", "\r\n", StringComparison.Ordinal)) == "claude-opus-5-5");
        Check(Read("event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n") is null);
        Check(Read(Events[..Events.IndexOf("event: message_stop", StringComparison.Ordinal)]) is null);
        Check(Read(Events.Replace("\"text\":\"OK\"", "\"text\":\"\"", StringComparison.Ordinal)) is null);
        Check(Read(Events + "event: error\ndata: {\"type\":\"error\"}\n\n") is null);
        Check(Read(Events.Replace("claude-opus-5-5", "fixture-secret", StringComparison.Ordinal)) is null);
        Check(Read(Events.Replace("\"stop_reason\":\"end_turn\"", "\"stop_reason\":null", StringComparison.Ordinal)) is null);
        return Task.CompletedTask;
    }
}
