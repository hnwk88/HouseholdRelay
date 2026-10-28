using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using HouseholdRelay;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

/// <summary>以真实 Kestrel 记录目的路径、认证头和请求体的可控上游。</summary>
sealed class UpstreamFixture : IAsyncDisposable
{
    public WebApplication App { get; }
    public string Url => App.Urls.Single();
    public ConcurrentQueue<UpstreamCall> Calls { get; } = new();
    public Func<HttpContext, Task>? Handler { get; set; }
    private UpstreamFixture(X509Certificate2? certificate)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] }); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => { if (certificate is not null) listen.UseHttps(certificate); }));
        App = builder.Build();
        App.Run(async context =>
        {
            using var reader = new StreamReader(context.Request.Body); var body = await reader.ReadToEndAsync(context.RequestAborted);
            Calls.Enqueue(new(context.Request.Path + context.Request.QueryString, context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase), body));
            if (Handler is not null) await Handler(context); else await Success(context);
        });
    }
    public static async Task<UpstreamFixture> Create(X509Certificate2? certificate = null)
    { var fixture = new UpstreamFixture(certificate); await fixture.App.StartAsync(); return fixture; }
    public static Task Success(HttpContext context)
    {
        if (context.Request.Path.Value!.EndsWith("/models", StringComparison.Ordinal)) return context.Response.WriteAsJsonAsync(new { data = new[] { new { id = "fixture-model" } } });
        if (context.Request.Path.Value!.EndsWith("/count_tokens", StringComparison.Ordinal)) return context.Response.WriteAsJsonAsync(new { input_tokens = 12 });
        return context.Response.WriteAsJsonAsync(new { id = "msg_fixture", type = "message", role = "assistant", model = "fixture-model", content = new[] { new { type = "text", text = "OK" } }, stop_reason = "end_turn" });
    }
    public UpstreamOptions Options(string suffix = "", string mode = "api-key", string authentication = "x-api-key") => new()
    { Preset = "compatible", BaseUrl = Url + suffix, AllowPrivateNetwork = true, Mode = mode, Authentication = authentication, Model = "fixture-model" };
    public async Task<string> Activate(UpstreamService service, string credential = "fixture-new-upstream-secret", UpstreamOptions? options = null)
    {
        var id = service.SaveCandidate(service.ReadActive().Version, options ?? Options(), credential);
        var report = await service.TestCandidate(id, true, CancellationToken.None);
        Tests.Check(report.CanActivate, report.Message); service.Activate(id); return id;
    }
    public static async Task<HttpResponseMessage> RelayRequest(WebFixture web, string token, string marker = "ordinary", string route = "/v1/messages?beta=true")
    {
        using var request = new HttpRequestMessage(route.StartsWith("/v1/models", StringComparison.Ordinal) ? HttpMethod.Get : HttpMethod.Post, web.Url + route)
        { Content = new StringContent(JsonSerializer.Serialize(new { model = "fixture-model", messages = new[] { new { role = "user", content = marker } } }), Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new("Bearer", token);
        request.Headers.Add("anthropic-beta", "future-beta,oauth-2025-04-20");
        request.Headers.Add("anthropic-version", "2023-06-01");
        return await web.Http.SendAsync(request);
    }
    public async ValueTask DisposeAsync() { await App.StopAsync(); await App.DisposeAsync(); }
}

sealed record UpstreamCall(string Path, Dictionary<string, string> Headers, string Body);
