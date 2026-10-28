using System.Net;
using System.Text;
using System.Text.Json;
using HouseholdRelay;
using Microsoft.AspNetCore.Http;
using static Tests;

static class UpstreamVersionTests
{
    public static async Task ProtocolModes()
    {
        await using var mock = await UpstreamFixture.Create(); await using var web = await WebFixture.Create();
        var service = new UpstreamService(web.Database); var user = web.Identities.Create("协议测试");
        foreach (var (mode, authentication, oauthBeta) in new[] { ("oauth", "bearer", true), ("oauth", "bearer", false), ("api-key", "x-api-key", false), ("api-key", "bearer", false) })
        {
            await mock.Activate(service, options: mock.Options("/gateway/v1?tenant=fixture", mode, authentication) with { OAuthBeta = oauthBeta });
            using var response = await UpstreamFixture.RelayRequest(web, user.Token);
            Check(response.IsSuccessStatusCode); var call = mock.Calls.Last();
            Check(call.Path == "/gateway/v1/messages?tenant=fixture&beta=true");
            Check(authentication == "bearer" ? call.Headers["Authorization"] == "Bearer fixture-new-upstream-secret" && !call.Headers.ContainsKey("x-api-key")
                : call.Headers["x-api-key"] == "fixture-new-upstream-secret" && !call.Headers.ContainsKey("Authorization"));
            Check(call.Headers["anthropic-beta"].Contains("future-beta"));
            Check(call.Headers["anthropic-beta"].Contains("oauth-") == oauthBeta);
            Check(call.Headers.Values.All(value => !value.Contains(user.Token)));
            var body = "{\"model\":\"fixture-model\",\"messages\":[{\"role\":\"assistant\",\"content\":[{\"type\":\"thinking\",\"thinking\":\"signed\",\"signature\":\"signature-value\"},{\"type\":\"tool_use\",\"id\":\"tool-id\",\"name\":\"Read\",\"input\":{}}]},{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"tool-id\",\"content\":\"result\",\"cache_control\":{\"type\":\"ephemeral\"}}]}],\"metadata\":{\"user_id\":\"user_old_account_a_session_s\"}}";
            using var message = new HttpRequestMessage(HttpMethod.Post, web.Url + "/v1/messages/count_tokens?beta=true") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            message.Headers.Authorization = new("Bearer", user.Token);
            using var count = await web.Http.SendAsync(message); Check(count.IsSuccessStatusCode);
            var sent = mock.Calls.Last(); Check(sent.Path.Contains("/gateway/v1/messages/count_tokens"));
            Check(sent.Body.Contains("signature-value") && sent.Body.Contains("cache_control") && sent.Body.Contains("tool_result"));
            if (mode == "api-key") Check(sent.Body == body, "API request bodies must not undergo account device normalization");
        }
        var persisted = new UpstreamService(new RelayDatabase(web.Home.Path)).ReadActive();
        Check(persisted.Report?.CanActivate == true && persisted.Options.Authentication == "bearer" && persisted.Credential == "fixture-new-upstream-secret");
    }

    public static async Task VersionIsolation(int oldStatus)
    {
        await using var first = await UpstreamFixture.Create(); await using var second = await UpstreamFixture.Create(); await using var web = await WebFixture.Create();
        var service = new UpstreamService(web.Database); var a = web.Identities.Create("甲"); var b = web.Identities.Create("乙");
        await first.Activate(service, "fixture-old-secret");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.Handler = async context =>
        { entered.TrySetResult(); await release.Task.WaitAsync(context.RequestAborted); context.Response.StatusCode = oldStatus; context.Response.Headers.RetryAfter = "60"; await context.Response.WriteAsJsonAsync(new { type = "error", error = new { type = "authentication_error", message = "old version" } }); };
        var old = UpstreamFixture.RelayRequest(web, a.Token, "old-inflight"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            await second.Activate(service, "fixture-fresh-secret");
            using var fresh = await UpstreamFixture.RelayRequest(web, b.Token, "new-version"); Check(fresh.IsSuccessStatusCode);
            release.TrySetResult(); using var previous = await old; Check((int)previous.StatusCode == oldStatus);
            using var after = await UpstreamFixture.RelayRequest(web, a.Token, "after-old-error"); Check(after.IsSuccessStatusCode);
            Check(first.Calls.Last().Headers["x-api-key"] == "fixture-old-secret" && second.Calls.Last().Headers["x-api-key"] == "fixture-fresh-secret");
            Check(first.Calls.Count == 3, "Old dispatch must never be replayed to the new upstream");
            Check(!web.Relay.Upstreams.Status(service.ReadActive().Version).Paused && web.Relay.Upstreams.Status(service.ReadActive().Version).CooldownSeconds == 0);
        }
        finally { release.TrySetResult(); }
    }

    public static async Task FailedDiagnosticIsolation()
    {
        await using var mock = await UpstreamFixture.Create(); await using var bad = await UpstreamFixture.Create(); await using var web = await WebFixture.Create();
        var service = new UpstreamService(web.Database); var user = web.Identities.Create("甲"); await mock.Activate(service);
        foreach (var status in new[] { 401, 403, 429, 307, 500 })
        {
            bad.Handler = async context => { context.Response.StatusCode = status; context.Response.Headers.Location = mock.Url + "/steal"; await context.Response.WriteAsync("secret-error-content"); };
            var id = service.SaveCandidate(service.ReadActive().Version, bad.Options(), "fixture-bad-secret");
            var report = await service.TestCandidate(id, true, CancellationToken.None); Check(!report.CanActivate);
            Check(!JsonSerializer.Serialize(report).Contains("secret-error-content"));
            using var response = await UpstreamFixture.RelayRequest(web, user.Token); Check(response.IsSuccessStatusCode);
            var rejected = false; try { service.Activate(id); } catch (InvalidOperationException) { rejected = true; } Check(rejected);
            Check(mock.Calls.All(call => !call.Path.Contains("steal")));
        }
        bad.Handler = context => context.Request.Path == "/v1/models"
            ? NotFound(context) : UpstreamFixture.Success(context);
        var absent = service.SaveCandidate(service.ReadActive().Version, bad.Options(), "fixture-no-model-catalog");
        var accepted = await service.TestCandidate(absent, true, CancellationToken.None);
        Check(accepted.CanActivate && accepted.ModelDiscovery == "unsupported" && accepted.Authentication == "passed");
        bad.Handler = context => context.Response.WriteAsJsonAsync(new { status = "ok" });
        var fake = service.SaveCandidate(service.ReadActive().Version, bad.Options(), "fixture-invalid-protocol");
        Check(!(await service.TestCandidate(fake, true, CancellationToken.None)).CanActivate, "HTTP 200 alone must not validate a model call");
        bad.Handler = UpstreamFixture.Success;
        var first = service.SaveCandidate(service.ReadActive().Version, bad.Options(), "fixture-first-candidate");
        var second = service.SaveCandidate(service.ReadActive().Version, bad.Options(), "fixture-second-candidate");
        await service.TestCandidate(first, true, CancellationToken.None); await service.TestCandidate(second, true, CancellationToken.None);
        service.Activate(first); var conflict = false; try { service.Activate(second); } catch (InvalidOperationException) { conflict = true; } Check(conflict);
    }
    private static Task NotFound(HttpContext context) { context.Response.StatusCode = 404; return Task.CompletedTask; }
}
