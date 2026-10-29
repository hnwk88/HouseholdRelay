using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using HouseholdRelay;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Tests;

if (args.FirstOrDefault() == "--real-upstream-browser") return await RealUpstreamValidation.Run(args);

var tests = new (string Name, Func<Task> Test)[]
{
    ("device normalization preserves sessions and prompt bytes", TestSuite.Metadata),
    ("host credentials/headers are shared; client sessions remain independent", TestSuite.Forwarding),
    ("access control, endpoint whitelist and upload limits", TestSuite.Rejections),
    ("SSE flush and cancellation release upstream resources", TestSuite.Streaming),
    ("two clients run concurrently and queued revocation is enforced", TestSuite.Concurrency),
    ("429 cools down all clients without replay", TestSuite.Cooldown),
    ("401/403 pause upstream authentication", TestSuite.AuthenticationPause),
    ("timeouts release capacity and redirects never leak credentials", TestSuite.TimeoutAndRedirect),
    ("DPAPI, TLS, independent client issuance and rotation", TestSuite.Storage),
    ("real Claude Code clients through TLS and a TCP tunnel to mock upstream", TestSuite.NativeClients),
    ("database identity, settings, sessions and restart persistence", FoundationTests.Persistence),
    ("legacy migration preserves credentials, identities and disabled tokens", FoundationTests.Migration),
    ("migration rollback and lost database fail closed", FoundationTests.Recovery),
    ("concurrent management commits and stable user limits", FoundationTests.ConcurrentWrites),
    ("failed permission writes do not publish partial revocation", FoundationTests.PermissionFailure),
    ("web bootstrap, authentication, CSRF, host and origin boundaries", FoundationTests.WebSecurity),
    ("web rate limit blocks setup aliases after canonical attempts", () => FoundationTests.WebRateLimitAliases(setup: true, alternating: false)),
    ("web rate limit blocks login aliases after canonical attempts", () => FoundationTests.WebRateLimitAliases(setup: false, alternating: false)),
    ("web rate limit shares setup budget across alternating aliases", () => FoundationTests.WebRateLimitAliases(setup: true, alternating: true)),
    ("web rate limit shares login budget across alternating aliases", () => FoundationTests.WebRateLimitAliases(setup: false, alternating: true)),
    ("web rate limit permits fifth attempt and keeps page budgets separate", FoundationTests.WebRateLimitValidAttempts),
    ("database token revocation at queue and upload boundaries", FoundationTests.Revocation),
    ("upstream address and URL contracts", UpstreamIntegrationTests.AddressContracts),
    ("API key mode isolates account headers", UpstreamIntegrationTests.AuthenticationModes),
    ("upstream JSON and SSE responses redact secrets", UpstreamIntegrationTests.ResponseSanitization),
    ("upstream candidate test and atomic activation", UpstreamIntegrationTests.CandidateLifecycle),
    ("restricted upstream requires explicit native diagnostic and preserves client requests", ClaudeCodeProbeTests.RestrictedUpstream),
    ("native diagnostic rejects incomplete and error streams", ClaudeCodeProbeTests.StreamValidation),
    ("native diagnostic cancellation releases its client and upstream", ClaudeCodeProbeTests.Cancellation),
    ("web directly configures, tests and activates an API upstream", UpstreamIntegrationTests.WebDirectConfiguration),
    ("upstream protocol modes, prefixes, query and restart persistence", UpstreamVersionTests.ProtocolModes),
    ("upstream version switch isolates old authentication failures", () => UpstreamVersionTests.VersionIsolation(401)),
    ("upstream version switch isolates old cooldown", () => UpstreamVersionTests.VersionIsolation(429)),
    ("upstream diagnostic failures cannot pause or activate over current version", UpstreamVersionTests.FailedDiagnosticIsolation),
    ("upstream connection, private address, TLS and cancellation boundaries", UpstreamSecurityTests.NetworkBoundaries),
    ("certificate validation, DPAPI import, restart and real TLS serving", UpstreamSecurityTests.CertificateImport),
    ("clean native client uses two connection variables and completes a tool conversation", CleanClientTests.ToolConversation),
    ("upstream HTTP CONNECT proxy uses validated destination addresses", UpstreamProxyTests.ConnectProxy),
    ("upstream schema upgrade rolls back and preserves existing identities", UpstreamMigrationTests.UpgradeRollback),
    ("usage identity, rotation, rename and private queries in both upstream modes", UsageIntegrationTests.IdentityAndModes),
    ("usage categories, null totals, partial JSON and idempotent completion", UsageIntegrationTests.CategoriesAndUnknown),
    ("usage authenticated rejections persist without dispatch", UsageIntegrationTests.Rejections),
    ("usage SSE byte fragments, cumulative counts, errors and missing endings", UsageStreamingTests.FragmentsAndEndings),
    ("usage cancellation and timeout preserve partial counts and release connections", UsageStreamingTests.CancellationAndTimeout),
    ("usage streaming memory stays bounded by individual events", UsageStreamingTests.BoundedEvents),
    ("usage busy and failed writes reject dispatch and expose completion failures", UsageStorageTests.WriteFailures),
    ("usage UTC retention boundary, hourly cleanup and restart recovery", UsageStorageTests.RetentionAndRecovery),
    ("usage schema migration rolls back before preserving identities", UsageStorageTests.MigrationRollback),
    ("usage indexed window queries, paging and concurrent management load", UsageConcurrencyTests.WindowQueriesAndLoad),
    ("usage queued rotation and dispatch persistence failures never send", UsageConcurrencyTests.QueueAndDispatchFailures),
    ("usage reported money, cumulative stream costs and currency isolation", UsageMoneyTests.ReportedCosts),
    ("usage price snapshots, decimal totals and local DST time buckets", UsageMoneyTests.PriceSnapshotsAndTimeZones),
    ("usage pricing forms enforce administrator, CSRF and version boundaries", UsageMoneyTests.PricingWebSecurity),
    ("usage graceful host shutdown drains and preserves completed requests", UsageRestartTests.GracefulRestart),
    ("private certificate lifecycle, version conflicts and expired startup rejection", PrivateCertificateTests.Lifecycle),
    ("private certificate web creation, CSRF and public-only trust download", PrivateCertificateTests.WebJourney),
    ("private certificate TLS rejects untrusted roots and wrong names", PrivateCertificateTests.Transport)
};
var certificatesBefore = TestCertificateStore.Snapshot();
var failures = 0;
var selected =tests.Where(item => args.Length == 0 || item.Name.Contains(args[0], StringComparison.OrdinalIgnoreCase)).ToArray();
foreach (var (name, test) in selected)
{
    try { await test().WaitAsync(TimeSpan.FromSeconds(75)); Console.WriteLine("PASS " + name); }
    // 单项失败或超过 75 秒只计数并打印，不中断后续测试，保证一次运行能看到全部失败项。
    catch (Exception error) { failures++; Console.WriteLine("FAIL " + name + "\n" + error); }
}
// 无论成败都清理本次运行新增的测试 CA，否则它们在用户证书存储里越积越多，最终让 TLS 链构建失败。
Console.WriteLine($"Removed {TestCertificateStore.RemoveAddedSince(certificatesBefore)} test CA certificate(s) added during this run.");
Console.WriteLine($"{selected.Length - failures}/{selected.Length} passed; model traffic used only local mock servers.");
return failures == 0 ? 0 : 1;

namespace Tests
{
static class TestSuite
{
    /// <summary>断言辅助：条件不成立时抛出带说明的异常，使当前测试被运行器记为失败。</summary>
    public static void Check(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
    /// <summary>轮询等待夹具条件成立：每 10 毫秒检查一次，最多约 2 秒，超时抛异常判失败。用于等待中转内部状态（如活动数、排队数）异步收敛。</summary>
    public static async Task Until(Func<bool> predicate)
    {
        for (var i = 0; i < 200; i++) { if (predicate()) return; await Task.Delay(10); }
        throw new Exception("Timed out waiting for fixture condition");
    }
    /// <summary>构造带 Claude Code 风格 metadata.user_id 的消息请求体，可指定提示内容、64 位设备号和会话名，供设备号归一化与会话隔离测试使用。</summary>
    private static string JsonBody(string marker = "private fixture prompt", string? device = null, string session = "session-one") => JsonSerializer.Serialize(new
    {
        model = "fixture", messages = new[] { new { role = "user", content = marker } },
        metadata = new { user_id = "user_" + (device ?? new string('a', 64)) + "_account_account-id_session_" + session }
    });

    /// <summary>
    /// 验证设备号归一化：旧式 user_id 字符串里的设备号被替换为中转统一设备号，会话名和提示不变；
    /// JSON 字符串形式的 user_id 只替换 device_id，会话号、账号和请求体其余字节原样保留；
    /// 无法识别的格式返回同一个字节数组，不做任何改写。
    /// </summary>
    public static Task Metadata()
    {
        var device = new string('c', 64); var input = Encoding.UTF8.GetBytes(JsonBody());
        var rewritten = Encoding.UTF8.GetString(DeviceMetadata.Normalize(input, device));
        Check(rewritten.Contains("user_" + device + "_account_account-id_session_session-one"));
        Check(rewritten.Contains("private fixture prompt"));
        var identifier = JsonSerializer.Serialize(new { device_id = "old", account_uuid = "account", session_id = "different-session" });
        var body = Encoding.UTF8.GetBytes("{ \"messages\": [ {\"content\":\"verbatim signed attribution\"} ], \"metadata\": {\"user_id\":" + JsonSerializer.Serialize(identifier) + "} }");
        var result = Encoding.UTF8.GetString(DeviceMetadata.Normalize(body, device));
        Check(result.StartsWith("{ \"messages\": [ {\"content\":\"verbatim signed attribution\"} ], \"metadata\": "));
        using var outer = JsonDocument.Parse(result);
        using var inner = JsonDocument.Parse(outer.RootElement.GetProperty("metadata").GetProperty("user_id").GetString()!);
        Check(inner.RootElement.GetProperty("device_id").GetString() == device);
        Check(inner.RootElement.GetProperty("session_id").GetString() == "different-session");
        Check(inner.RootElement.GetProperty("account_uuid").GetString() == "account");
        var unknown = Encoding.UTF8.GetBytes("{\"metadata\":{\"user_id\":\"unknown-future-format\"}}");
        Check(ReferenceEquals(unknown, DeviceMetadata.Normalize(unknown, device)));
        return Task.CompletedTask;
    }

    /// <summary>
    /// 验证宿主凭据与请求头统一、客户端会话独立：两个成员的请求都带上游密钥和统一 User-Agent，
    /// x-stainless-os、x-forwarded-for 被剥离，anthropic-beta 在保留客户端能力的同时加上 oauth 标记，
    /// 上游响应的 request-id 透传回客户端，请求体换成中转设备号但各自会话号不变；
    /// 并确认日志中不含上游密钥、提示内容和个人令牌。
    /// </summary>
    public static async Task Forwarding()
    {
        await using var f = await Fixture.Create(async c => { c.Response.Headers["request-id"] = "fixture-request"; await c.Response.WriteAsync("{}"); });
        foreach (var name in new[] { "study", "bedroom" })
        {
            using var request = f.Request(name, body: JsonBody(device: new string(name == "study" ? 'a' : 'b', 64), session: name));
            request.Headers.TryAddWithoutValidation("User-Agent", "different-" + name);
            request.Headers.TryAddWithoutValidation("x-stainless-os", name);
            request.Headers.TryAddWithoutValidation("x-forwarded-for", "private-address");
            request.Headers.TryAddWithoutValidation("anthropic-beta", "future-capability");
            using var response = await f.Http.SendAsync(request); Check(response.StatusCode == HttpStatusCode.OK);
            Check(response.Headers.GetValues("request-id").Single() == "fixture-request");
        }
        Check(f.Calls.Count == 2);
        foreach (var call in f.Calls)
        {
            Check(call.Headers["Authorization"] == "Bearer fixture-upstream-secret");
            Check(call.Headers["User-Agent"] == f.Config.UpstreamUserAgent);
            Check(!call.Headers.ContainsKey("x-stainless-os") && !call.Headers.ContainsKey("x-forwarded-for"));
            Check(call.Headers["anthropic-beta"].Contains("future-capability") && call.Headers["anthropic-beta"].Contains("oauth-2025-04-20"));
            Check(call.Body.Contains(f.Config.DeviceId));
        }
        Check(f.Calls.Any(c => c.Body.Contains("session_study")) && f.Calls.Any(c => c.Body.Contains("session_bedroom")));
        var logs = string.Join("\n", f.Logs);
        Check(!logs.Contains("fixture-upstream-secret") && !logs.Contains("private fixture prompt") && !logs.Contains("study-test-token"));
    }

    /// <summary>
    /// 验证入口拒绝边界：未知令牌 401、非白名单路径 404、非法 JSON 400、超过 512 字节上传限制 413；
    /// 全部客户端停用后返回 401，且这些请求都不会到达上游。
    /// </summary>
    public static async Task Rejections()
    {
        await using var f = await Fixture.Create(c => c.Response.WriteAsync("{}"), c => c.MaxBodyBytes = 512);
        using var bad = await f.Send("unknown"); Check((int)bad.StatusCode == 401);
        using var route = await f.Send(path: "/elsewhere"); Check((int)route.StatusCode == 404);
        using var malformed = await f.Send(body: "{bad"); Check((int)malformed.StatusCode == 400);
        using var huge = await f.Send(body: new string('x', 513)); Check((int)huge.StatusCode == 413);
        f.Relay.Clients.Replace(f.Config.Clients.Select(c => c with { Enabled = false }).ToArray());
        using var disabled = await f.Send(); Check((int)disabled.StatusCode == 401); Check(f.Calls.IsEmpty);
    }

    /// <summary>
    /// 验证 SSE 流式转发：上游首个事件立即刷新到客户端；客户端取消并释放响应后，
    /// 取消必须在 3 秒内传播到模拟上游，且中转的活动请求数回到 0。
    /// </summary>
    public static async Task Streaming()
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var f = await Fixture.Create(async c =>
        {
            c.Response.ContentType = "text/event-stream"; await c.Response.WriteAsync("data: first\n\n"); await c.Response.Body.FlushAsync();
            try { await Task.Delay(Timeout.Infinite, c.RequestAborted); }
            // 客户端断开后中转必须中止上游请求，这里捕获到取消即证明取消已传播到模拟上游。
            catch (OperationCanceledException) { cancelled.TrySetResult(); }
        });
        using var abort = new CancellationTokenSource(); using var request = f.Request();
        using var response = await f.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, abort.Token);
        await using var stream = await response.Content.ReadAsStreamAsync(); var buffer = new byte[100];
        var count = await stream.ReadAsync(buffer); Check(Encoding.UTF8.GetString(buffer, 0, count) == "data: first\n\n");
        // 主动取消并释放响应来模拟客户端中途断开，再等待上游收到取消，3 秒内没收到即判失败。
        abort.Cancel(); response.Dispose(); await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Until(() => f.Relay.Admission.Active == 0);
    }

    /// <summary>
    /// 验证并发与排队吊销：两个客户端同时占用两个活动名额，第三个请求进入排队；
    /// 排队期间停用全部客户端后，已在途的两个请求正常完成，排队请求返回 401，上游只收到 2 次调用。
    /// </summary>
    public static async Task Concurrency()
    {
        var releases = new ConcurrentQueue<TaskCompletionSource>();
        await using var f = await Fixture.Create(async c =>
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); releases.Enqueue(release);
            await release.Task.WaitAsync(c.RequestAborted); await c.Response.WriteAsync("{}");
        });
        var first = f.Send("study"); var second = f.Send("bedroom");
        await Until(() => releases.Count == 2); Check(f.Relay.Admission.Active == 2);
        var third = f.Send("study"); await Until(() => f.Relay.Admission.Queued == 1);
        f.Relay.Clients.Replace(f.Config.Clients.Select(c => c with { Enabled = false }).ToArray());
        foreach (var release in releases) release.SetResult();
        using var a = await first; using var b = await second; using var c = await third;
        Check((int)c.StatusCode == 401); Check(f.Calls.Count == 2);
    }

    /// <summary>
    /// 验证 429 全局冷却：上游返回 429 后，另一位成员的后续请求直接得到 429 和正数 Retry-After，
    /// 上游只收到 1 次调用，中转不会重放请求。
    /// </summary>
    public static async Task Cooldown()
    {
        await using var f = await Fixture.Create(async c => { c.Response.StatusCode = 429; c.Response.Headers.RetryAfter = "10"; await c.Response.WriteAsync("{}"); });
        using var a = await f.Send(); using var b = await f.Send("bedroom");
        Check((int)a.StatusCode == 429 && (int)b.StatusCode == 429 && f.Calls.Count == 1);
        Check(b.Headers.RetryAfter?.Delta > TimeSpan.Zero);
    }

    /// <summary>
    /// 验证上游认证失败暂停：上游返回 401 或 403 时首个请求原样返回该状态码，
    /// 后续请求直接返回 503 且不再访问上游（上游只收到 1 次调用）。
    /// </summary>
    public static async Task AuthenticationPause()
    {
        foreach (var status in new[] { 401, 403 })
        {
            await using var f = await Fixture.Create(async c => { c.Response.StatusCode = status; await c.Response.WriteAsync("{}"); });
            using var a = await f.Send(); using var b = await f.Send();
            Check((int)a.StatusCode == status && (int)b.StatusCode == 503 && f.Calls.Count == 1);
        }
    }

    /// <summary>
    /// 验证超时与重定向边界：上游 1 秒不响应时返回 504 且释放活动名额；
    /// 上游返回 307 重定向时中转不跟随，返回 502 且上游只收到 1 次调用，防止凭据被带到重定向目标。
    /// </summary>
    public static async Task TimeoutAndRedirect()
    {
        await using (var f = await Fixture.Create(c => Task.Delay(Timeout.Infinite, c.RequestAborted), c => c.RequestTimeoutSeconds = 1))
        { using var response = await f.Send(); Check((int)response.StatusCode == 504); Check(f.Relay.Admission.Active == 0); }
        await using (var f = await Fixture.Create(c => { c.Response.StatusCode = 307; c.Response.Headers.Location = "http://127.0.0.1:1/steal"; return Task.CompletedTask; }))
        { using var response = await f.Send(); Check((int)response.StatusCode == 502); Check(f.Calls.Count == 1); }
    }

    /// <summary>
    /// 验证本机存储与身份签发：DPAPI 密文不同于明文且能解密还原；初始化后签发三个客户端身份，
    /// 新安装不导出明文令牌目录；可停用单个客户端；轮换后旧令牌失效、新令牌可用；保存的 TLS 证书带私钥可加载。
    /// </summary>
    public static async Task Storage()
    {
        var root = TempDirectory();
        try
        {
            var secret = Encoding.UTF8.GetBytes("fixture-not-a-real-account"); var encrypted = WindowsSecrets.Protect(secret);
            Check(!encrypted.AsSpan().SequenceEqual(secret)); Check(WindowsSecrets.Unprotect(encrypted).AsSpan().SequenceEqual(secret));
            var process = new TestProcess(root); var manager = process.Get<ManagementCommands>(); await manager.Initialize("localhost", 5433, 8787);
            var identities = process.Identities;
            identities.Create("书房", "study"); var issued = identities.Create("卧室", "bedroom"); identities.Create("公司", "office");
            var config = manager.Read(); Check(config.Clients.Count == 3);
            Check(!Directory.Exists(Path.Combine(root, "clients")), "New installations must not export all plaintext tokens");
            manager.ManageClient("disable", "office"); Check(!manager.Read().Clients.Single(c => c.Name == "office").Enabled);
            var changed = identities.Rotate(issued.UserId); Check(issued.Token != changed.Token);
            Check(identities.Clients.Authenticate(issued.Token) is null && identities.Clients.Authenticate(changed.Token) is not null);
            var pfx = process.Settings.LoadCertificate()!;
            using var certificate = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.EphemeralKeySet);
            Check(certificate.HasPrivateKey); CryptographicOperations.ZeroMemory(pfx);
        }
        // 无论成败都删除临时目录，避免加密配置和数据库残留在系统临时文件夹。
        finally { RemoveTemp(root); }
    }

    /// <summary>
    /// 端到端验证真实 Claude Code 客户端：中转用本地 CA 签发的证书提供 TLS，客户端经原始 TCP 隧道访问并信任 relay-ca.pem；
    /// 两个客户端并发提问并各自得到对应答复，上游调用全部使用宿主密钥，且两人的提示不会出现在同一次上游请求中。
    /// </summary>
    public static async Task NativeClients()
    {
        using var generated = TestProcess.CertificateFactory.CreateCertificate("localhost", out var authorityPem);
        using var certificate = TestProcess.CertificateFactory.LoadServerCertificate(generated.Export(X509ContentType.Pfx));
        using var authority = X509Certificate2.CreateFromPem(authorityPem);
        await using var f = await Fixture.Create(async c =>
        {
            var call = c.Items["body"]!.ToString()!;
            if (c.Request.Path == "/v1/messages/count_tokens") { await c.Response.WriteAsJsonAsync(new { input_tokens = 12 }); return; }
            if (c.Request.Path == "/v1/models") { await c.Response.WriteAsJsonAsync(new { data = Array.Empty<object>(), has_more = false }); return; }
            var marker = call.Contains("FIXTURE_BEDROOM") ? "BEDROOM_OK" : "STUDY_OK";
            using var body = JsonDocument.Parse(call); var model = body.RootElement.GetProperty("model").GetString();
            var message = new { id = "msg_fixture", type = "message", role = "assistant", model, content = Array.Empty<object>(),
                stop_reason = (string?)null, stop_sequence = (string?)null, usage = new { input_tokens = 12, output_tokens = 1 } };
            c.Response.ContentType = "text/event-stream";
            async Task Event(string type, object value) => await c.Response.WriteAsync($"event: {type}\ndata: {JsonSerializer.Serialize(value)}\n\n");
            await Event("message_start", new { type = "message_start", message });
            await Event("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "" } });
            await Event("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text = marker } });
            await Event("content_block_stop", new { type = "content_block_stop", index = 0 });
            await Event("message_delta", new { type = "message_delta", delta = new { stop_reason = "end_turn", stop_sequence = (string?)null }, usage = new { output_tokens = 4 } });
            await Event("message_stop", new { type = "message_stop" });
        }, certificate: certificate, certificateChain: new X509Certificate2Collection(authority));
        await using var tunnel = new Tunnel(new Uri(f.Url).Port);
        using (var handler = new HttpClientHandler { UseProxy = false })
        {
            handler.ServerCertificateCustomValidationCallback = (_, remote, _, errors) =>
            {
                if (remote is null || (errors & System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch) != 0) return false;
                using var chain = new X509Chain(); chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(authority); chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                return chain.Build(remote);
            };
            using var probe = new HttpClient(handler);
            using var response = await probe.GetAsync($"https://127.0.0.1:{tunnel.Port}/healthz");
            Check(response.IsSuccessStatusCode, "TLS-over-TCP health probe failed");
        }
        var root = TempDirectory();
        try
        {
            async Task Client(string name, string marker, string expected)
            {
                var folder = Path.Combine(root, name); Directory.CreateDirectory(folder);
                ConfigurationFiles.Write(Path.Combine(folder, "client.json"), new ClientBundle($"https://127.0.0.1:{tunnel.Port}", "hr_" + name + "-test-token"));
                File.WriteAllText(Path.Combine(folder, "relay-ca.pem"), authorityPem);
                var debugPath = Path.Combine(folder, "fixture-debug.log");
                var start = TestProcess.ClientLauncher.Prepare(folder, ["--debug-file", debugPath, "--safe-mode", "--no-session-persistence", "--strict-mcp-config", "--no-chrome", "--tools", "Read", "--model", "claude-sonnet-4-6", "--print", "Reply with " + marker]);
                start.WorkingDirectory = folder; start.RedirectStandardOutput = true; start.RedirectStandardError = true; start.RedirectStandardInput = true; start.CreateNoWindow = true;
                using var child = new Process { StartInfo = start }; using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var stdout = new StringBuilder(); var stderr = new StringBuilder();
                child.OutputDataReceived += (_, line) => { if (line.Data is not null) stdout.AppendLine(line.Data); };
                child.ErrorDataReceived += (_, line) => { if (line.Data is not null) stderr.AppendLine(line.Data); };
                child.Start(); child.StandardInput.Close(); child.BeginOutputReadLine(); child.BeginErrorReadLine();
                try
                {
                    await child.WaitForExitAsync(timeout.Token);
                    Check(child.ExitCode == 0, $"Native CLI failed: {stderr} {stdout}");
                    Check(stdout.ToString().Contains(expected), "Unexpected native CLI result: " + stdout);
                }
                // 30 秒超时触发取消时，汇总上游调用数、隧道连接数和调试日志再抛出，便于定位 TLS 或代理问题。
                catch (OperationCanceledException)
                {
                    var debug = File.Exists(debugPath) ? string.Join("\n", File.ReadLines(debugPath).Where(line => line.Contains("cert", StringComparison.OrdinalIgnoreCase) || line.Contains("ERROR") || line.Contains("TLS") || line.Contains("proxy", StringComparison.OrdinalIgnoreCase)).Take(35)) : "No debug file";
                    throw new Exception($"Native CLI timed out. Calls: {f.Calls.Count}. Tunnel accepts: {tunnel.Accepted}. Output: {stdout} {stderr}. Relay logs: {string.Join(";", f.Logs)}. Debug: {debug}");
                }
                // 超时或断言失败时子进程可能仍在运行，必须连同进程树一起结束，防止残留 Claude Code 进程。
                finally { if (!child.HasExited) { child.Kill(true); await child.WaitForExitAsync(); } }
            }
            f.Relay.Clients.Replace(f.Config.Clients.Select(c => c with { TokenHash = ConfigurationFiles.Hash("hr_" + c.Name + "-test-token") }).ToArray());
            // 两个客户端并发运行，验证共用同一上游身份时各自会话和答复互不串扰。
            await Task.WhenAll(Client("study", "FIXTURE_STUDY", "STUDY_OK"), Client("bedroom", "FIXTURE_BEDROOM", "BEDROOM_OK"));
            Check(f.Calls.Count >= 2); Check(f.Calls.All(c => c.Headers["Authorization"] == "Bearer fixture-upstream-secret"));
            Check(f.Calls.All(c => !(c.Body.Contains("FIXTURE_STUDY") && c.Body.Contains("FIXTURE_BEDROOM"))));
        }
        // 无论成败都删除客户端临时目录，避免测试令牌和证书文件残留。
        finally { RemoveTemp(root); }
    }

    /// <summary>在系统临时目录下创建带固定前缀和随机名的测试目录，并返回其路径。</summary>
    private static string TempDirectory() { var path = Path.Combine(Path.GetTempPath(), "household-relay-csharp-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    /// <summary>删除测试临时目录：先确认目录直接位于系统临时目录下且带测试前缀，校验不通过就抛异常而不删除。</summary>
    private static void RemoveTemp(string path)
    {
        var absolute = Path.GetFullPath(path);
        Check(Path.GetDirectoryName(absolute) == Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())));
        Check(Path.GetFileName(absolute).StartsWith("household-relay-csharp-test-", StringComparison.Ordinal));
        // 只有通过上面两项路径校验才递归删除，防止传错路径时误删其他目录。
        Directory.Delete(absolute, true);
    }
}

sealed record Call(Dictionary<string, string> Headers, string Body);
sealed class Fixture : IAsyncDisposable
{
    /// <summary>本次夹具的中转配置，含 study、bedroom、office 三个测试客户端的令牌哈希，测试可据此改写客户端列表。</summary>
    public required RelayConfiguration Config { get; init; }
    /// <summary>被测中转服务实例，测试通过它读取活动与排队计数或替换客户端列表。</summary>
    public required RelayServer Relay { get; init; }
    /// <summary>在本机回环随机端口上运行的中转主机。</summary>
    public required WebApplication App { get; init; }
    /// <summary>模拟 Claude 上游的本机主机，记录每次请求后交给测试传入的处理委托作答。</summary>
    public required WebApplication Upstream { get; init; }
    /// <summary>中转主机的实际监听地址。</summary>
    public required string Url { get; init; }
    /// <summary>访问中转用的 HTTP 客户端，关闭自动重定向和系统代理，保证看到中转的原始响应。</summary>
    public required HttpClient Http { get; init; }
    /// <summary>模拟上游收到的全部请求（请求头与请求体），用于断言转发内容和调用次数。</summary>
    public required ConcurrentQueue<Call> Calls { get; init; }
    /// <summary>中转写出的日志文本，用于断言日志中没有泄露密钥、提示或令牌。</summary>
    public required ConcurrentQueue<string> Logs { get; init; }
    /// <summary>
    /// 创建一套完整夹具：先在本机随机端口启动记录请求的模拟上游，再用三个测试客户端的配置启动中转（可选 TLS 证书与证书链），
    /// 中转的上游指向该模拟上游，所有模型流量都不离开本机。
    /// </summary>
    public static async Task<Fixture> Create(RequestDelegate handler, Action<RelayConfiguration>? configure = null, X509Certificate2? certificate = null, ClientRegistry? clients = null, X509Certificate2Collection? certificateChain = null)
    {
        var calls = new ConcurrentQueue<Call>(); var logs = new ConcurrentQueue<string>();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] }); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0)); var upstream = builder.Build();
        upstream.Run(async context =>
        {
            using var reader = new StreamReader(context.Request.Body); var body = await reader.ReadToEndAsync(); context.Items["body"] = body;
            calls.Enqueue(new Call(context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase), body));
            try { await handler(context); } catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { /* 中转中止上游请求属于预期行为，只吞掉由请求中止引起的取消，其他异常照常暴露给测试。 */ }
        });
        await upstream.StartAsync();
        var config = new RelayConfiguration { UpstreamUserAgent = "claude-cli/fixture (external, cli)", Clients =
            [new("study", ConfigurationFiles.Hash("study-test-token")), new("bedroom", ConfigurationFiles.Hash("bedroom-test-token")), new("office", ConfigurationFiles.Hash("office-test-token"))] };
        configure?.Invoke(config);
        var services = TestProcess.StandaloneRelay(config, "fixture-upstream-secret", new Uri(upstream.Urls.Single()), clients, new FixtureLog(logs));
        var relay = services.GetRequiredService<RelayServer>();
        var app = relay.Build(certificate, 0, certificateChain); await app.StartAsync();
        return new Fixture { Config = config, Relay = relay, App = app, Upstream = upstream, Url = app.Urls.Single(), Calls = calls, Logs = logs,
            Http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false }) };
    }
    /// <summary>构造发往中转的 POST 请求，默认路径 /v1/messages?beta=true，按名字携带对应测试客户端的 Bearer 令牌；调用方负责释放。</summary>
    public HttpRequestMessage Request(string name = "study", string path = "/v1/messages?beta=true", string body = "{\"model\":\"fixture\",\"messages\":[]}")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Url + path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new("Bearer", name + "-test-token"); return request;
    }
    /// <summary>用 <see cref="Request"/> 构造请求并立即发送，返回中转响应；调用方负责释放响应。</summary>
    public async Task<HttpResponseMessage> Send(string name = "study", string path = "/v1/messages?beta=true", string body = "{\"model\":\"fixture\",\"messages\":[]}")
    { using var request = Request(name, path, body); return await Http.SendAsync(request); }
    /// <summary>依次释放 HTTP 客户端、停止并释放中转主机和中转服务，最后停止模拟上游。</summary>
    public async ValueTask DisposeAsync()
    {
        Http.Dispose(); await App.StopAsync(); await App.DisposeAsync(); Relay.Dispose(); await Upstream.StopAsync(); await Upstream.DisposeAsync();
    }
}

// Models a raw TCP port mapping, preserving TLS from the native client to the relay.
sealed class Tunnel : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentBag<Task> connections = [];
    private readonly Task loop;
    private int accepted;
    /// <summary>隧道已接受的 TCP 连接数，超时诊断时用来判断客户端是否连上了隧道。</summary>
    public int Accepted => Volatile.Read(ref accepted);
    /// <summary>隧道在本机回环地址上实际监听的端口。</summary>
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    /// <summary>开始在随机端口监听，并启动后台接受循环，把连接转发到本机的目标端口。</summary>
    public Tunnel(int destination) { listener.Start(); loop = Accept(destination); }
    /// <summary>循环接受客户端连接，计数后交给 <see cref="Forward"/> 转发，直到隧道停止。</summary>
    private async Task Accept(int destination)
    {
        try { while (!stop.IsCancellationRequested) { var client = await listener.AcceptTcpClientAsync(stop.Token); Interlocked.Increment(ref accepted); connections.Add(Forward(client, destination)); } }
        // 释放隧道时取消接受循环属于正常退出，不视为错误。
        catch (OperationCanceledException) { }
    }
    /// <summary>在客户端连接与本机目标端口之间双向复制原始字节，不解密 TLS，模拟家用路由器的端口映射。</summary>
    private async Task Forward(TcpClient client, int destination)
    {
        using (client)
        using (var upstream = new TcpClient())
        // 每条连接用关联取消源：隧道停止或任一方向结束时都能同时关闭两个方向的复制。
        using (var cancel = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
        {
            try
            {
                await upstream.ConnectAsync(IPAddress.Loopback, destination, cancel.Token);
                var a = client.GetStream().CopyToAsync(upstream.GetStream(), cancel.Token);
                var b = upstream.GetStream().CopyToAsync(client.GetStream(), cancel.Token);
                // 任一方向结束就取消另一方向，避免半关闭的连接一直挂起。
                await Task.WhenAny(a, b); cancel.Cancel();
                try { await Task.WhenAll(a, b); } catch (OperationCanceledException) { /* 另一方向会因上面的取消而抛出取消异常，这是预期结果，忽略它以免把正常关闭误报为转发失败。 */ }
            }
            // 对端重置、隧道关闭等网络异常只结束这一条转发，不影响其他连接和测试结果。
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException) { }
        }
    }
    /// <summary>先取消并停止监听，再等待接受循环和全部转发任务结束，最后释放取消源，保证不遗留后台连接。</summary>
    // 先取消再等待：取消会同时终止接受循环和每条连接的复制，等全部任务结束后才释放取消源，避免任务仍在使用已释放的令牌。
    public async ValueTask DisposeAsync() { stop.Cancel(); listener.Stop(); await loop; await Task.WhenAll(connections); stop.Dispose(); }
}
}
