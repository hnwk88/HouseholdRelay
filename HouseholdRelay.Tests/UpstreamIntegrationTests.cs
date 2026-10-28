using System.Net;
using System.Text;
using HouseholdRelay;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using static Tests;

/// <summary>验证地址合同、认证模式、候选激活及响应脱敏。</summary>
static class UpstreamIntegrationTests
{
    public static Task AddressContracts()
    {
        var root = UpstreamAddress.Normalize("https://gateway.example.test/team/v1/?tenant=relay", false);
        Check(root.AbsoluteUri == "https://gateway.example.test/team/?tenant=relay");
        var endpoint = UpstreamAddress.Endpoint(new UpstreamOptions { BaseUrl = root.AbsoluteUri }, "/v1/messages", "?beta=true");
        Check(endpoint.AbsoluteUri == "https://gateway.example.test/team/v1/messages?tenant=relay&beta=true");
        CheckThrows(() => UpstreamAddress.Normalize("https://user:password@gateway.example.test", false));
        CheckThrows(() => UpstreamAddress.Normalize("https://gateway.example.test/?api_key=fixture", false));
        CheckThrows(() => UpstreamAddress.Normalize("http://gateway.example.test", false));
        Check(UpstreamAddress.Normalize("http://127.0.0.1:8080/prefix", true).AbsoluteUri == "http://127.0.0.1:8080/prefix/");
        CheckThrows(() => UpstreamAddress.Proxy("http://user:secret@127.0.0.1:8080"));
        return Task.CompletedTask;
    }

    public static async Task AuthenticationModes()
    {
        await using var fixture = await Fixture.Create(c => c.Response.WriteAsync("{}"), config => config.AuthMode = "api-key");
        using var response = await fixture.Send();
        Check(response.IsSuccessStatusCode);
        var call = fixture.Calls.Single();
        Check(call.Headers["x-api-key"] == "fixture-upstream-secret");
        Check(!call.Headers.ContainsKey("Authorization") && !call.Headers.ContainsKey("anthropic-beta"));
    }

    public static async Task ResponseSanitization()
    {
        await using var fixture = await Fixture.Create(async context =>
        {
            context.Response.StatusCode = 400; context.Response.ContentType = "application/json";
            context.Response.Headers["x-request-id"] = "request-fixture"; context.Response.Headers["x-secret-header"] = "fixture-upstream-secret";
            context.Response.Headers["request-id"] = "fixture-upstream-secret"; context.Response.Headers["x-should-retry"] = "false";
            context.Response.Headers.SetCookie = "session=fixture-upstream-secret";
            await context.Response.WriteAsync("{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"thinking is not supported: \\u0066ixture-upstream-secret\"}}");
        });
        using var response = await fixture.Send(); var body = await response.Content.ReadAsStringAsync();
        Check(response.StatusCode == HttpStatusCode.BadRequest && body.Contains("invalid_request_error") && !body.Contains("fixture-upstream-secret"));
        Check(response.Headers.GetValues("x-request-id").Single() == "request-fixture");
        Check(!response.Headers.Contains("x-secret-header") && !response.Headers.Contains("Set-Cookie") && !response.Headers.Contains("request-id"));
        Check(response.Headers.GetValues("x-should-retry").Single() == "false" && body.Contains("thinking is not supported"));

        await using var stream = await Fixture.Create(async context =>
        {
            context.Response.StatusCode = 400; context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync("event: error\n"); await context.Response.Body.FlushAsync();
            await context.Response.WriteAsync("data: {\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"fixture-up");
            await context.Response.Body.FlushAsync(); await Task.Delay(20);
            await context.Response.WriteAsync("stream-secret\"}}\n\n");
        });
        using var streamed = await stream.Send(); var streamedBody = await streamed.Content.ReadAsStringAsync();
        Check(streamedBody.Contains("event: error") && streamedBody.Contains("invalid_request_error") && !streamedBody.Contains("fixture-upstream-secret"));
    }

    public static async Task CandidateLifecycle()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] }); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0)); var mock = builder.Build();
        mock.Run(async context =>
        {
            context.Response.ContentType = "application/json";
            if (context.Request.Path.Value!.EndsWith("/models", StringComparison.Ordinal)) await context.Response.WriteAsync("{\"data\":[{\"id\":\"fixture-model\"}]}");
            else await context.Response.WriteAsync("{\"id\":\"msg_fixture\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"fixture-model\",\"content\":[],\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}");
        });
        await mock.StartAsync();
        try
        {
            using var home = new TestHome(); var database = new RelayDatabase(home.Path); var service = new UpstreamService(database);
            var active = service.ReadActive();
            var options = new UpstreamOptions { Mode = "api-key", Authentication = "x-api-key", Preset = "compatible", BaseUrl = mock.Urls.Single(), AllowPrivateNetwork = true, Model = "fixture-model" };
            var id = service.SaveCandidate(active.Version, options, "fixture-api-secret");
            Check(service.ReadActive().Credential is null && service.ReadCandidate(id).State == "untested");
            var refused = false;
            try { await service.TestCandidate(id, false, CancellationToken.None); }
            catch (InvalidOperationException) { refused = true; }
            Check(refused, "A diagnostic call requires explicit consent");
            var report = await service.TestCandidate(id, true, CancellationToken.None);
            Check(report.CanActivate && report.Connection == "passed" && report.Authentication == "passed" && report.VerifiedModel == "fixture-model");
            service.Activate(id); var activated = service.ReadActive();
            Check(activated.Configured && activated.Options.Mode == "api-key" && activated.Options.BaseUrl == options.Validated().BaseUrl);
            Check(activated.Report?.CanActivate == true);
            CheckThrows(() => service.SaveCandidate(active.Version, options, "fixture-api-secret-2"));
        }
        finally { await mock.StopAsync(); await mock.DisposeAsync(); }
    }

    public static async Task WebDirectConfiguration()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] }); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0)); var mock = builder.Build();
        mock.Run(async context =>
        {
            context.Response.ContentType = "application/json";
            if (context.Request.Path.Value!.EndsWith("/models", StringComparison.Ordinal)) await context.Response.WriteAsync("{\"data\":[{\"id\":\"fixture-model\"}]}");
            else await context.Response.WriteAsync("{\"id\":\"msg_fixture\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"fixture-model\",\"content\":[]}");
        });
        await mock.StartAsync();
        try
        {
            await using var fixture = await WebFixture.Create(); await fixture.Login();
            var csrf = await fixture.Csrf("/Upstream"); var values = new Dictionary<string, string>
            {
                ["Input.Mode"] = "api-key", ["Input.Preset"] = "compatible", ["Input.BaseUrl"] = mock.Urls.Single(),
                ["Input.Authentication"] = "x-api-key", ["Input.AllowPrivateNetwork"] = "true", ["Input.Model"] = "fixture-model",
                ["Credential"] = "fixture-page-secret", ["Version"] = new UpstreamService(fixture.Database).ReadActive().Version.ToString(),
                ["__RequestVerificationToken"] = csrf
            };
            using var saved = await fixture.Post("/Upstream?handler=Save", values);
            var html = WebUtility.HtmlDecode(await saved.Content.ReadAsStringAsync()); Check(saved.IsSuccessStatusCode && html.Contains("候选已保存") && !html.Contains("fixture-page-secret"));
            var candidate = new UpstreamService(fixture.Database).LatestCandidate()!;
            values = new() { ["candidateId"] = candidate.Id, ["confirmed"] = "true", ["__RequestVerificationToken"] = await fixture.Csrf("/Upstream") };
            using var tested = await fixture.Post("/Upstream?handler=Test", values); html = WebUtility.HtmlDecode(await tested.Content.ReadAsStringAsync());
            Check(tested.IsSuccessStatusCode && html.Contains("最小模型调用成功") && !html.Contains("fixture-page-secret"));
            values = new() { ["candidateId"] = candidate.Id, ["__RequestVerificationToken"] = await fixture.Csrf("/Upstream") };
            using var activated = await fixture.Post("/Upstream?handler=Activate", values); Check(activated.IsSuccessStatusCode);
            Check((new UpstreamService(fixture.Database).ReadActive()).Configured);
        }
        finally { await mock.StopAsync(); await mock.DisposeAsync(); }
    }

    private static void CheckThrows(Action action) { try { action(); throw new Exception("Expected rejection"); } catch (InvalidOperationException) { } }
}
