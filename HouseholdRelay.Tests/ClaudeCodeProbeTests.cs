using System.Net;
using System.Text;
using System.Text.Json;
using HouseholdRelay;
using Microsoft.AspNetCore.Http;
using static Tests;

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

    public static async Task RestrictedUpstream()
    {
        await using var mock = await UpstreamFixture.Create(); await using var web = await WebFixture.Create();
        var service = new UpstreamService(web.Database);
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
        var persisted = new UpstreamService(new RelayDatabase(web.Home.Path)).ReadActive();
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
            catch (OperationCanceledException) { released.TrySetResult(); }
        };
        var service = new UpstreamService(web.Database); var version = service.ReadActive().Version;
        var id = service.SaveCandidate(version, mock.Options() with { Diagnostic = "claude-code", Model = "claude-opus-5-5" }, "fixture-cancel-secret");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var pending = service.TestCandidate(id, true, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20)); cancellation.Cancel();
        var report = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        await released.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Check(report.Call == "cancelled" && !report.CanActivate && service.ReadActive().Version == version, report.Call + ": " + report.Message);
        Check(mock.Calls.Count(call => call.Path.Contains("/messages")) == 1);
    }

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
