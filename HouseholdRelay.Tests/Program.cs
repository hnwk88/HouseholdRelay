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

if (args.FirstOrDefault() == "--real-upstream-browser") return await RealUpstreamValidation.Run(args);

var tests = new (string Name, Func<Task> Test)[]
{
    ("device normalization preserves sessions and prompt bytes", Tests.Metadata),
    ("host credentials/headers are shared; client sessions remain independent", Tests.Forwarding),
    ("access control, endpoint whitelist and upload limits", Tests.Rejections),
    ("SSE flush and cancellation release upstream resources", Tests.Streaming),
    ("two clients run concurrently and queued revocation is enforced", Tests.Concurrency),
    ("429 cools down all clients without replay", Tests.Cooldown),
    ("401/403 pause upstream authentication", Tests.AuthenticationPause),
    ("timeouts release capacity and redirects never leak credentials", Tests.TimeoutAndRedirect),
    ("DPAPI, TLS, independent client issuance and rotation", Tests.Storage),
    ("real Claude Code clients through TLS and a TCP tunnel to mock upstream", Tests.NativeClients),
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
var failures = 0;
var selected = tests.Where(item => args.Length == 0 || item.Name.Contains(args[0], StringComparison.OrdinalIgnoreCase)).ToArray();
foreach (var (name, test) in selected)
{
    try { await test().WaitAsync(TimeSpan.FromSeconds(75)); Console.WriteLine("PASS " + name); }
    catch (Exception error) { failures++; Console.WriteLine("FAIL " + name + "\n" + error); }
}
Console.WriteLine($"{selected.Length - failures}/{selected.Length} passed; model traffic used only local mock servers.");
return failures == 0 ? 0 : 1;

static class Tests
{
    public static void Check(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
    public static async Task Until(Func<bool> predicate)
    {
        for (var i = 0; i < 200; i++) { if (predicate()) return; await Task.Delay(10); }
        throw new Exception("Timed out waiting for fixture condition");
    }
    private static string JsonBody(string marker = "private fixture prompt", string? device = null, string session = "session-one") => JsonSerializer.Serialize(new
    {
        model = "fixture", messages = new[] { new { role = "user", content = marker } },
        metadata = new { user_id = "user_" + (device ?? new string('a', 64)) + "_account_account-id_session_" + session }
    });

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

    public static async Task Streaming()
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var f = await Fixture.Create(async c =>
        {
            c.Response.ContentType = "text/event-stream"; await c.Response.WriteAsync("data: first\n\n"); await c.Response.Body.FlushAsync();
            try { await Task.Delay(Timeout.Infinite, c.RequestAborted); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); }
        });
        using var abort = new CancellationTokenSource(); using var request = f.Request();
        using var response = await f.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, abort.Token);
        await using var stream = await response.Content.ReadAsStreamAsync(); var buffer = new byte[100];
        var count = await stream.ReadAsync(buffer); Check(Encoding.UTF8.GetString(buffer, 0, count) == "data: first\n\n");
        abort.Cancel(); response.Dispose(); await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Until(() => f.Relay.Admission.Active == 0);
    }

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

    public static async Task Cooldown()
    {
        await using var f = await Fixture.Create(async c => { c.Response.StatusCode = 429; c.Response.Headers.RetryAfter = "10"; await c.Response.WriteAsync("{}"); });
        using var a = await f.Send(); using var b = await f.Send("bedroom");
        Check((int)a.StatusCode == 429 && (int)b.StatusCode == 429 && f.Calls.Count == 1);
        Check(b.Headers.RetryAfter?.Delta > TimeSpan.Zero);
    }

    public static async Task AuthenticationPause()
    {
        foreach (var status in new[] { 401, 403 })
        {
            await using var f = await Fixture.Create(async c => { c.Response.StatusCode = status; await c.Response.WriteAsync("{}"); });
            using var a = await f.Send(); using var b = await f.Send();
            Check((int)a.StatusCode == status && (int)b.StatusCode == 503 && f.Calls.Count == 1);
        }
    }

    public static async Task TimeoutAndRedirect()
    {
        await using (var f = await Fixture.Create(c => Task.Delay(Timeout.Infinite, c.RequestAborted), c => c.RequestTimeoutSeconds = 1))
        { using var response = await f.Send(); Check((int)response.StatusCode == 504); Check(f.Relay.Admission.Active == 0); }
        await using (var f = await Fixture.Create(c => { c.Response.StatusCode = 307; c.Response.Headers.Location = "http://127.0.0.1:1/steal"; return Task.CompletedTask; }))
        { using var response = await f.Send(); Check((int)response.StatusCode == 502); Check(f.Calls.Count == 1); }
    }

    public static async Task Storage()
    {
        var root = TempDirectory();
        try
        {
            var secret = Encoding.UTF8.GetBytes("fixture-not-a-real-account"); var encrypted = WindowsSecrets.Protect(secret);
            Check(!encrypted.AsSpan().SequenceEqual(secret)); Check(WindowsSecrets.Unprotect(encrypted).AsSpan().SequenceEqual(secret));
            var manager = new Management(root); await manager.Initialize("localhost", 5433, 8787);
            var identities = new IdentityService(manager.Database);
            identities.Create("书房", "study"); var issued = identities.Create("卧室", "bedroom"); identities.Create("公司", "office");
            var config = manager.Read(); Check(config.Clients.Count == 3);
            Check(!Directory.Exists(Path.Combine(root, "clients")), "New installations must not export all plaintext tokens");
            manager.ManageClient("disable", "office"); Check(!manager.Read().Clients.Single(c => c.Name == "office").Enabled);
            var changed = identities.Rotate(issued.UserId); Check(issued.Token != changed.Token);
            Check(identities.Clients.Authenticate(issued.Token) is null && identities.Clients.Authenticate(changed.Token) is not null);
            var pfx = new ConfigurationService(manager.Database).LoadCertificate()!;
            using var certificate = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.EphemeralKeySet);
            Check(certificate.HasPrivateKey); CryptographicOperations.ZeroMemory(pfx);
        }
        finally { RemoveTemp(root); }
    }

    public static async Task NativeClients()
    {
        using var generated = Management.CreateCertificate("localhost", out var authorityPem);
        using var certificate = Management.LoadServerCertificate(generated.Export(X509ContentType.Pfx));
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
                var start = ClientLauncher.Prepare(folder, ["--debug-file", debugPath, "--safe-mode", "--no-session-persistence", "--strict-mcp-config", "--no-chrome", "--tools", "Read", "--model", "claude-sonnet-4-6", "--print", "Reply with " + marker]);
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
                catch (OperationCanceledException)
                {
                    var debug = File.Exists(debugPath) ? string.Join("\n", File.ReadLines(debugPath).Where(line => line.Contains("cert", StringComparison.OrdinalIgnoreCase) || line.Contains("ERROR") || line.Contains("TLS") || line.Contains("proxy", StringComparison.OrdinalIgnoreCase)).Take(35)) : "No debug file";
                    throw new Exception($"Native CLI timed out. Calls: {f.Calls.Count}. Tunnel accepts: {tunnel.Accepted}. Output: {stdout} {stderr}. Relay logs: {string.Join(";", f.Logs)}. Debug: {debug}");
                }
                finally { if (!child.HasExited) { child.Kill(true); await child.WaitForExitAsync(); } }
            }
            f.Relay.Clients.Replace(f.Config.Clients.Select(c => c with { TokenHash = ConfigurationFiles.Hash("hr_" + c.Name + "-test-token") }).ToArray());
            await Task.WhenAll(Client("study", "FIXTURE_STUDY", "STUDY_OK"), Client("bedroom", "FIXTURE_BEDROOM", "BEDROOM_OK"));
            Check(f.Calls.Count >= 2); Check(f.Calls.All(c => c.Headers["Authorization"] == "Bearer fixture-upstream-secret"));
            Check(f.Calls.All(c => !(c.Body.Contains("FIXTURE_STUDY") && c.Body.Contains("FIXTURE_BEDROOM"))));
        }
        finally { RemoveTemp(root); }
    }

    private static string TempDirectory() { var path = Path.Combine(Path.GetTempPath(), "household-relay-csharp-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static void RemoveTemp(string path)
    {
        var absolute = Path.GetFullPath(path);
        Check(Path.GetDirectoryName(absolute) == Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())));
        Check(Path.GetFileName(absolute).StartsWith("household-relay-csharp-test-", StringComparison.Ordinal));
        Directory.Delete(absolute, true);
    }
}

sealed record Call(Dictionary<string, string> Headers, string Body);
sealed class Fixture : IAsyncDisposable
{
    public required RelayConfiguration Config { get; init; }
    public required RelayServer Relay { get; init; }
    public required WebApplication App { get; init; }
    public required WebApplication Upstream { get; init; }
    public required string Url { get; init; }
    public required HttpClient Http { get; init; }
    public required ConcurrentQueue<Call> Calls { get; init; }
    public required ConcurrentQueue<string> Logs { get; init; }
    public static async Task<Fixture> Create(RequestDelegate handler, Action<RelayConfiguration>? configure = null, X509Certificate2? certificate = null, ClientRegistry? clients = null, X509Certificate2Collection? certificateChain = null)
    {
        var calls = new ConcurrentQueue<Call>(); var logs = new ConcurrentQueue<string>();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] }); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0)); var upstream = builder.Build();
        upstream.Run(async context =>
        {
            using var reader = new StreamReader(context.Request.Body); var body = await reader.ReadToEndAsync(); context.Items["body"] = body;
            calls.Enqueue(new Call(context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase), body));
            try { await handler(context); } catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        });
        await upstream.StartAsync();
        var config = new RelayConfiguration { UpstreamUserAgent = "claude-cli/fixture (external, cli)", Clients =
            [new("study", ConfigurationFiles.Hash("study-test-token")), new("bedroom", ConfigurationFiles.Hash("bedroom-test-token")), new("office", ConfigurationFiles.Hash("office-test-token"))] };
        configure?.Invoke(config);
        var relay = new RelayServer(config, "fixture-upstream-secret", item => logs.Enqueue(JsonSerializer.Serialize(item)), new Uri(upstream.Urls.Single()), clients);
        var app = relay.Build(certificate, 0, certificateChain); await app.StartAsync();
        return new Fixture { Config = config, Relay = relay, App = app, Upstream = upstream, Url = app.Urls.Single(), Calls = calls, Logs = logs,
            Http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false }) };
    }
    public HttpRequestMessage Request(string name = "study", string path = "/v1/messages?beta=true", string body = "{\"model\":\"fixture\",\"messages\":[]}")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Url + path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new("Bearer", name + "-test-token"); return request;
    }
    public async Task<HttpResponseMessage> Send(string name = "study", string path = "/v1/messages?beta=true", string body = "{\"model\":\"fixture\",\"messages\":[]}")
    { using var request = Request(name, path, body); return await Http.SendAsync(request); }
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
    public int Accepted => Volatile.Read(ref accepted);
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public Tunnel(int destination) { listener.Start(); loop = Accept(destination); }
    private async Task Accept(int destination)
    {
        try { while (!stop.IsCancellationRequested) { var client = await listener.AcceptTcpClientAsync(stop.Token); Interlocked.Increment(ref accepted); connections.Add(Forward(client, destination)); } }
        catch (OperationCanceledException) { }
    }
    private async Task Forward(TcpClient client, int destination)
    {
        using (client)
        using (var upstream = new TcpClient())
        using (var cancel = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
        {
            try
            {
                await upstream.ConnectAsync(IPAddress.Loopback, destination, cancel.Token);
                var a = client.GetStream().CopyToAsync(upstream.GetStream(), cancel.Token);
                var b = upstream.GetStream().CopyToAsync(client.GetStream(), cancel.Token);
                await Task.WhenAny(a, b); cancel.Cancel();
                try { await Task.WhenAll(a, b); } catch (OperationCanceledException) { }
            }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException) { }
        }
    }
    public async ValueTask DisposeAsync() { stop.Cancel(); listener.Stop(); await loop; await Task.WhenAll(connections); stop.Dispose(); }
}
