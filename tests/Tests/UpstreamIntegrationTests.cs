using System.Net;
using System.Text;
using HouseholdRelay;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using static Tests.TestSuite;

namespace Tests;

/// <summary>验证地址合同、认证模式、候选激活及响应脱敏。</summary>
static class UpstreamIntegrationTests
{
    /// <summary>
    /// 验证上游地址合同：基础地址末尾的 /v1/ 被去掉并保留查询串，拼接端点时合并原查询串与请求查询串；
    /// 带用户名密码、查询串含 api_key、公网明文 HTTP 的地址都被拒绝；允许私有网络时本机 HTTP 可用并补上结尾斜杠；
    /// 带凭据的代理地址被拒绝。
    /// </summary>
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

    /// <summary>验证 API 密钥模式隔离账号头：上游只收到 x-api-key 形式的宿主密钥，不带 Authorization，也不带账号模式专用的 anthropic-beta。</summary>
    public static async Task AuthenticationModes()
    {
        await using var fixture = await Fixture.Create(c => c.Response.WriteAsync("{}"), config => config.AuthMode = "api-key");
        using var response = await fixture.Send();
        Check(response.IsSuccessStatusCode);
        var call = fixture.Calls.Single();
        Check(call.Headers["x-api-key"] == "fixture-upstream-secret");
        Check(!call.Headers.ContainsKey("Authorization") && !call.Headers.ContainsKey("anthropic-beta"));
    }

    /// <summary>
    /// 验证上游响应脱敏：JSON 错误体中的上游密钥（含 Unicode 转义写法）被抹去而错误类型和提示保留；
    /// 安全的 x-request-id、x-should-retry 透传，含密钥的自定义头、request-id 和 Set-Cookie 被丢弃；
    /// SSE 错误流中被拆在两个分块里的密钥同样被抹去。
    /// </summary>
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

    /// <summary>
    /// 验证候选上游的测试与原子激活：保存候选不影响当前上游且状态为未测试；未明确同意时拒绝发起诊断调用；
    /// 同意后连接、认证和模型检查均通过才可激活，激活后当前上游采用候选配置；用过期版本号再保存候选被拒绝。
    /// </summary>
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
            using var home = new TestHome(); var service = new TestProcess(home.Path).Upstreams;
            var active = service.ReadActive();
            var options = new UpstreamOptions { Mode = "api-key", Authentication = "x-api-key", Preset = "compatible", BaseUrl = mock.Urls.Single(), AllowPrivateNetwork = true, Model = "fixture-model" };
            var id = service.SaveCandidate(active.Version, options, "fixture-api-secret");
            Check(service.ReadActive().Credential is null && service.ReadCandidate(id).State == "untested");
            var refused = false;
            try { await service.TestCandidate(id, false, CancellationToken.None); }
            // 诊断会向上游发起真实的最小模型调用，未经明确同意必须拒绝；捕获到即记为拒绝成功。
            catch (InvalidOperationException) { refused = true; }
            Check(refused, "A diagnostic call requires explicit consent");
            var report = await service.TestCandidate(id, true, CancellationToken.None);
            Check(report.CanActivate && report.Connection == "passed" && report.Authentication == "passed" && report.VerifiedModel == "fixture-model");
            service.Activate(id); var activated = service.ReadActive();
            Check(activated.Configured && activated.Options.Mode == "api-key" && activated.Options.BaseUrl == options.Validated().BaseUrl);
            Check(activated.Report?.CanActivate == true);
            CheckThrows(() => service.SaveCandidate(active.Version, options, "fixture-api-secret-2"));
        }
        // 无论断言成败都停止模拟上游，释放端口，避免影响后续测试。
        finally { await mock.StopAsync(); await mock.DisposeAsync(); }
    }

    /// <summary>
    /// 验证管理网页只填名称、地址、密钥接入 API 上游：空名称被拒且不保存；「获取模型」保存并读出目录与自动探测的认证方式，页面不回显密钥；
    /// 从下拉框选模型后按写明费用的测试按钮，读句说明认证方式、检测方式和模型；按名称切换后当前上游带上该名称。
    /// </summary>
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
            Dictionary<string, string> Form(string name) => new()
            {
                ["Api.Name"] = name, ["Api.BaseUrl"] = mock.Urls.Single(), ["Api.Credential"] = "fixture-page-secret", ["Api.AllowPrivateNetwork"] = "true",
                ["Version"] = fixture.Upstreams.ReadActive().Version.ToString()
            };
            var unnamed = Form(""); unnamed["__RequestVerificationToken"] = await fixture.Csrf("/Upstream");
            using (var refused = await fixture.Post("/Upstream?handler=DiscoverApi", unnamed))
            {
                var page = WebUtility.HtmlDecode(await refused.Content.ReadAsStringAsync());
                Check(refused.IsSuccessStatusCode && page.Contains("请填写供应商名称") && !page.Contains("fixture-page-secret") && fixture.Upstreams.LatestCandidate() is null);
            }
            var values = Form("页面测试"); values["__RequestVerificationToken"] = await fixture.Csrf("/Upstream");
            using var saved = await fixture.Post("/Upstream?handler=DiscoverApi", values);
            var html = WebUtility.HtmlDecode(await saved.Content.ReadAsStringAsync());
            Check(saved.IsSuccessStatusCode && html.Contains("已保存「页面测试」。已获取 1 个模型，认证用 x-api-key") && html.Contains("<option value=\"fixture-model\"") && !html.Contains("fixture-page-secret"), html);
            var candidate = fixture.Upstreams.LatestCandidate()!;
            Check(candidate.Name == "页面测试" && candidate.Options.Authentication == "x-api-key" && !candidate.Options.AuthenticationManual && candidate.State == "untested");
            values = new() { ["candidateId"] = candidate.Id, ["model"] = "fixture-model", ["__RequestVerificationToken"] = await fixture.Csrf("/Upstream") };
            using var tested = await fixture.Post("/Upstream?handler=Test", values); html = WebUtility.HtmlDecode(await tested.Content.ReadAsStringAsync());
            Check(tested.IsSuccessStatusCode && html.Contains("认证用 x-api-key，按标准 Messages 方式通过，模型 fixture-model。") && !html.Contains("fixture-page-secret"));
            Check(html.Contains("测试（会消耗少量上游额度）") && !html.Contains("type=\"checkbox\" name=\"confirmed\""));
            values = new() { ["candidateId"] = candidate.Id, ["Version"] = fixture.Upstreams.ReadActive().Version.ToString(), ["__RequestVerificationToken"] = await fixture.Csrf("/Upstream") };
            using var activated = await fixture.Post("/Upstream?handler=Activate", values); html = WebUtility.HtmlDecode(await activated.Content.ReadAsStringAsync());
            Check(activated.IsSuccessStatusCode && html.Contains("已切换到「页面测试」"));
            var active = fixture.Upstreams.ReadActive();
            Check(active.Configured && active.CandidateId == candidate.Id && active.Name == "页面测试" && active.Options.Model == "fixture-model");
        }
        // 无论断言成败都停止模拟上游，释放端口，避免影响后续测试。
        finally { await mock.StopAsync(); await mock.DisposeAsync(); }
    }

    /// <summary>断言给定操作以 InvalidOperationException 被拒绝；操作正常返回时抛出普通异常判失败。</summary>
    private static void CheckThrows(Action action) { try { action(); throw new Exception("Expected rejection"); } catch (InvalidOperationException) { /* 只接受预期的拒绝异常；上面的“未拒绝”异常不是此类型，会继续抛出使测试失败。 */ } }
}
