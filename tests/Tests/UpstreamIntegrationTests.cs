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
        await using var fixture = await Fixture.Create(c => c.Response.WriteAsync("{}"), config => config.AuthMode = Core.Upstream.UpstreamMode.ApiKey);
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
    /// 验证中转按 Content-Encoding 解开上游压缩响应：gzip、deflate(zlib)、br、zstd 四种各发一次非流式 JSON，客户端拿到的是解开后的明文、脱敏照常；
    /// 再用 gzip 压一条 SSE 事件流，逐事件解开转发。解开用纯托管 ZstdSharp（zstd）与 .NET 内置流（其余），不带原生库。
    /// </summary>
    public static async Task ResponseDecompression()
    {
        foreach (var encoding in new[] { "gzip", "deflate", "br", "zstd" })
        {
            await using var fixture = await Fixture.Create(async context =>
            {
                context.Response.ContentType = "application/json"; context.Response.Headers.ContentEncoding = encoding;
                var payload = Encoding.UTF8.GetBytes("{\"type\":\"message\",\"model\":\"fixture-model\",\"leak\":\"fixture-upstream-secret\",\"usage\":{\"input_tokens\":7,\"output_tokens\":9}}");
                await context.Response.Body.WriteAsync(Compress(encoding, payload));
            });
            using var response = await fixture.Send(); var body = await response.Content.ReadAsStringAsync();
            Check(response.IsSuccessStatusCode, encoding + " response status " + response.StatusCode);
            Check(body.Contains("\"input_tokens\":7", StringComparison.Ordinal) && body.Contains("fixture-model", StringComparison.Ordinal), encoding + " body must arrive decompressed: " + body[..Math.Min(80, body.Length)]);
            Check(!body.Contains("fixture-upstream-secret", StringComparison.Ordinal), encoding + " secret must still be redacted after decompression");
            Check(!response.Content.Headers.Contains("Content-Encoding"), encoding + " downstream body is identity, not re-compressed");
        }
        await using var sse = await Fixture.Create(async context =>
        {
            context.Response.ContentType = "text/event-stream"; context.Response.Headers.ContentEncoding = "gzip";
            var events = Encoding.UTF8.GetBytes("event: message_stop\ndata: {\"type\":\"message_stop\",\"leak\":\"fixture-upstream-secret\"}\n\n");
            await context.Response.Body.WriteAsync(Compress("gzip", events));
        });
        using var streamed = await sse.Send(); var streamedBody = await streamed.Content.ReadAsStringAsync();
        Check(streamedBody.Contains("event: message_stop", StringComparison.Ordinal) && streamedBody.Contains("message_stop", StringComparison.Ordinal), "gzip SSE decoded: " + streamedBody);
        Check(!streamedBody.Contains("fixture-upstream-secret", StringComparison.Ordinal), "gzip SSE secret redacted after decompression");
    }

    /// <summary>用指定编码压缩一段字节：zstd 走纯托管 ZstdSharp，gzip/deflate(zlib)/br 走 .NET 内置流，与中转解压侧一一对应。</summary>
    internal static byte[] Compress(string encoding, byte[] payload)
    {
        using var output = new MemoryStream();
        if (encoding == "zstd") { using var comp = new ZstdSharp.Compressor(3); return comp.Wrap(payload).ToArray(); }
        using (Stream codec = encoding switch
        {
            "gzip" => new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionMode.Compress, true),
            "deflate" => new System.IO.Compression.ZLibStream(output, System.IO.Compression.CompressionMode.Compress, true),
            "br" => new System.IO.Compression.BrotliStream(output, System.IO.Compression.CompressionMode.Compress, true),
            _ => throw new ArgumentOutOfRangeException(nameof(encoding))
        }) codec.Write(payload);
        return output.ToArray();
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
            var active = await service.ReadActive();
            var options = new UpstreamOptions { Mode = Core.Upstream.UpstreamMode.ApiKey, Authentication = Core.Upstream.AuthenticationScheme.XApiKey, Preset = Core.Upstream.UpstreamPreset.Compatible, BaseUrl = mock.Urls.Single(), AllowPrivateNetwork = true, Model = "fixture-model" };
            var id = await service.SaveCandidate(active.ActiveVersion, options, "fixture-api-secret");
            Check((await service.ReadActive()).Credential is null && (await service.ReadCandidate(id)).State == Core.Upstream.CandidateState.Untested);
            var refused = false;
            try { await service.TestCandidate(id, false, CancellationToken.None); }
            // 诊断会向上游发起真实的最小模型调用，未经明确同意必须拒绝；捕获到即记为拒绝成功。
            catch (InvalidOperationException) { refused = true; }
            Check(refused, "A diagnostic call requires explicit consent");
            var report = await service.TestCandidate(id, true, CancellationToken.None);
            Check(report.CanActivate && report.Connection == Core.Upstream.CheckStatus.Passed && report.Authentication == Core.Upstream.CheckStatus.Passed && report.VerifiedModel == "fixture-model");
            // 通过后改选模型就回到未测试，旧报告还在；切换必须按「状态不是通过」读出还没通过测试（领域规则 UpstreamCandidate.EnsureActivatable），而不是只看报告。
            await service.ChooseModel(id, "other-model");
            var untested = "";
            try { await service.Activate(id); } catch (InvalidOperationException error) { /* 未测试的上游必须拒绝切换；记下读句，下面核对是哪一句。 */ untested = error.Message; }
            Check(untested == "这个上游还没有通过最小模型调用测试，不能切换。请先测试。", "Switching to an untested upstream must read the not-tested sentence: " + untested);
            await service.ChooseModel(id, "fixture-model");
            report = await service.TestCandidate(id, true, CancellationToken.None);
            Check(report.CanActivate, "Testing again after choosing the model back must pass");
            await service.Activate(id); var activated = await service.ReadActive();
            Check(activated.Configured && activated.Options.Mode == Core.Upstream.UpstreamMode.ApiKey && activated.Options.BaseUrl == options.Validated().BaseUrl);
            Check(activated.Report?.CanActivate == true);
            await CheckThrows(() => service.SaveCandidate(active.ActiveVersion, options, "fixture-api-secret-2"));
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
            async Task<Dictionary<string, string>> Form(string name) => new()
            {
                ["Api.Name"] = name, ["Api.BaseUrl"] = mock.Urls.Single(), ["Api.Credential"] = "fixture-page-secret", ["Api.AllowPrivateNetwork"] = "true",
                ["Version"] = (await fixture.Upstreams.ReadActive()).ActiveVersion.ToString()
            };
            var unnamed = await Form(""); unnamed["__RequestVerificationToken"] = await fixture.Csrf("/Upstream");
            using (var refused = await fixture.Post("/Upstream?handler=DiscoverApi", unnamed))
            {
                var page = WebUtility.HtmlDecode(await refused.Content.ReadAsStringAsync());
                Check(refused.IsSuccessStatusCode && page.Contains("请填写供应商名称") && !page.Contains("fixture-page-secret") && await fixture.Upstreams.LatestCandidate() is null);
            }
            var values = await Form("页面测试"); values["__RequestVerificationToken"] = await fixture.Csrf("/Upstream");
            using var saved = await fixture.Post("/Upstream?handler=DiscoverApi", values);
            var html = WebUtility.HtmlDecode(await saved.Content.ReadAsStringAsync());
            Check(saved.IsSuccessStatusCode && html.Contains("已保存「页面测试」。已获取 1 个模型，认证用 x-api-key") && html.Contains("<option value=\"fixture-model\"") && !html.Contains("fixture-page-secret"), html);
            var candidate = (await fixture.Upstreams.LatestCandidate())!;
            Check(candidate.Name == "页面测试" && candidate.Options.Authentication == Core.Upstream.AuthenticationScheme.XApiKey && !candidate.Options.AuthenticationManual && candidate.State == Core.Upstream.CandidateState.Untested);
            values = new() { ["candidateId"] = candidate.Id.ToString(CultureInfo.InvariantCulture), ["model"] = "fixture-model", ["__RequestVerificationToken"] = await fixture.Csrf("/Upstream") };
            using var tested = await fixture.Post("/Upstream?handler=Test", values); html = WebUtility.HtmlDecode(await tested.Content.ReadAsStringAsync());
            Check(tested.IsSuccessStatusCode && html.Contains("认证用 x-api-key，按标准 Messages 方式通过，模型 fixture-model。") && !html.Contains("fixture-page-secret"));
            Check(html.Contains("测试（会消耗少量上游额度）") && !html.Contains("type=\"checkbox\" name=\"confirmed\""));
            values = new() { ["candidateId"] = candidate.Id.ToString(CultureInfo.InvariantCulture), ["Version"] = (await fixture.Upstreams.ReadActive()).ActiveVersion.ToString(), ["__RequestVerificationToken"] = await fixture.Csrf("/Upstream") };
            using var activated = await fixture.Post("/Upstream?handler=Activate", values); html = WebUtility.HtmlDecode(await activated.Content.ReadAsStringAsync());
            Check(activated.IsSuccessStatusCode && html.Contains("已切换到「页面测试」"));
            var active = await fixture.Upstreams.ReadActive();
            Check(active.Configured && active.UpstreamCandidateId == candidate.Id && active.Name == "页面测试" && active.Options.Model == "fixture-model");
        }
        // 无论断言成败都停止模拟上游，释放端口，避免影响后续测试。
        finally { await mock.StopAsync(); await mock.DisposeAsync(); }
    }

    /// <summary>断言给定操作以 InvalidOperationException 被拒绝；操作正常返回时抛出普通异常判失败。</summary>
    private static void CheckThrows(Action action) { try { action(); throw new Exception("Expected rejection"); } catch (InvalidOperationException) { /* 只接受预期的拒绝异常；上面的“未拒绝”异常不是此类型，会继续抛出使测试失败。 */ } }
    /// <summary>与上一个同样的拒绝断言，给返回 Task 的调用用：等它做完才能接住异步抛出的拒绝。</summary>
    private static async Task CheckThrows(Func<Task> action) { try { await action(); throw new Exception("Expected rejection"); } catch (InvalidOperationException) { /* 只接受预期的拒绝异常；上面的“未拒绝”异常不是此类型，会继续抛出使测试失败。 */ } }
}
