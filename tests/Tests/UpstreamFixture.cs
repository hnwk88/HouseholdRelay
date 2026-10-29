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

namespace Tests;

/// <summary>以真实 Kestrel 记录目的路径、认证头和请求体的可控上游。</summary>
sealed class UpstreamFixture : IAsyncDisposable
{
    /// <summary>在本机回环随机端口运行的模拟上游主机（传入证书时为 HTTPS）。</summary>
    public WebApplication App { get; }
    /// <summary>模拟上游的实际监听地址，作为候选上游的 BaseUrl。</summary>
    public string Url => App.Urls.Single();
    /// <summary>模拟上游收到的每次请求（路径含查询串、请求头、请求体），用于断言中转转发了什么。</summary>
    public ConcurrentQueue<UpstreamCall> Calls { get; } = new();
    /// <summary>测试可替换的应答逻辑；为空时用 <see cref="Success"/> 给出标准成功响应。</summary>
    public Func<HttpContext, Task>? Handler { get; set; }
    /// <summary>构建模拟上游（尚未启动）：先完整读入并记录请求，再交给 <see cref="Handler"/> 或默认成功应答。</summary>
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
    /// <summary>构建并启动模拟上游；传入证书时以 HTTPS 提供服务。</summary>
    public static async Task<UpstreamFixture> Create(X509Certificate2? certificate = null)
    { var fixture = new UpstreamFixture(certificate); await fixture.App.StartAsync(); return fixture; }
    /// <summary>默认成功应答：/models 返回含 fixture-model 的模型列表，/count_tokens 返回 12 个输入令牌，其余返回一条内容为 OK 的完整消息。</summary>
    public static Task Success(HttpContext context)
    {
        if (context.Request.Path.Value!.EndsWith("/models", StringComparison.Ordinal)) return context.Response.WriteAsJsonAsync(new { data = new[] { new { id = "fixture-model" } } });
        if (context.Request.Path.Value!.EndsWith("/count_tokens", StringComparison.Ordinal)) return context.Response.WriteAsJsonAsync(new { input_tokens = 12 });
        return context.Response.WriteAsJsonAsync(new { id = "msg_fixture", type = "message", role = "assistant", model = "fixture-model", content = new[] { new { type = "text", text = "OK" } }, stop_reason = "end_turn" });
    }
    /// <summary>生成指向本模拟上游的兼容型候选配置（允许私有网络，模型 fixture-model），可指定路径后缀、模式和认证方式。</summary>
    public UpstreamOptions Options(string suffix = "", string mode = "api-key", string authentication = "x-api-key") => new()
    { Preset = "compatible", BaseUrl = Url + suffix, AllowPrivateNetwork = true, Mode = mode, Authentication = authentication, Model = "fixture-model" };
    /// <summary>按当前版本保存候选上游，经明确同意后做诊断测试，测试必须允许激活，然后激活并返回候选编号。</summary>
    public async Task<string> Activate(IUpstreamService service, string credential = "fixture-new-upstream-secret", UpstreamOptions? options = null)
    {
        var id = service.SaveCandidate(service.ReadActive().Version, options ?? Options(), credential);
        var report = await service.TestCandidate(id, true, CancellationToken.None);
        TestSuite.Check(report.CanActivate, report.Message); service.Activate(id); return id;
    }
    /// <summary>
    /// 以家庭成员令牌经管理网页主机上的中转发请求：/v1/models 路由用 GET，其余用 POST 带最小消息体；
    /// 附带客户端常见的 anthropic-beta 与 anthropic-version 头，供断言中转如何改写。调用方负责释放响应。
    /// </summary>
    public static async Task<HttpResponseMessage> RelayRequest(WebFixture web, string token, string marker = "ordinary", string route = "/v1/messages?beta=true")
    {
        using var request = new HttpRequestMessage(route.StartsWith("/v1/models", StringComparison.Ordinal) ? HttpMethod.Get : HttpMethod.Post, web.Url + route)
        { Content = new StringContent(JsonSerializer.Serialize(new { model = "fixture-model", messages = new[] { new { role = "user", content = marker } } }), Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new("Bearer", token);
        request.Headers.Add("anthropic-beta", "future-beta,oauth-2025-04-20");
        request.Headers.Add("anthropic-version", "2023-06-01");
        return await web.Http.SendAsync(request);
    }
    /// <summary>停止并释放模拟上游主机。</summary>
    public async ValueTask DisposeAsync() { await App.StopAsync(); await App.DisposeAsync(); }
}

sealed record UpstreamCall(string Path, Dictionary<string, string> Headers, string Body);
