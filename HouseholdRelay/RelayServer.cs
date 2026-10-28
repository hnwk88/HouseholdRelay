using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace HouseholdRelay;

public sealed class RelayServer : IDisposable
{
    private static readonly Dictionary<string, string> Routes = new()
    { ["/v1/messages"] = "POST", ["/v1/messages/count_tokens"] = "POST", ["/v1/models"] = "GET" };
    private readonly RelayConfiguration config;
    private readonly Action<object> log;
    private long completed;
    public ClientRegistry Clients { get; }
    public Admission Admission { get; }
    public UpstreamPool Upstreams { get; }
    public bool HttpsActive { get; internal set; }
    public UsageStore? Usage { get; private set; }

    internal UsageStore EnableUsage(RelayDatabase database) => Usage ??= new UsageStore(database, log: log);

    public RelayServer(RelayConfiguration config, string? credential, Action<object>? log = null, Uri? testUpstream = null, ClientRegistry? clients = null, UpstreamService? upstreams = null)
    {
        config.Validate();
        if (credential is not null && (string.IsNullOrWhiteSpace(credential) || credential.Any(char.IsControl))) throw new InvalidOperationException("账号令牌格式错误。");
        if (testUpstream is not null && !(testUpstream.Scheme == "http" && testUpstream.Host == "127.0.0.1"))
            throw new InvalidOperationException("模拟上游只允许本机 HTTP。");
        this.config = config; this.log = log ?? (_ => { });
        Clients = clients ?? new ClientRegistry(config.Clients); Admission = new Admission(config);
        var options = UpstreamOptions.Legacy(config, testUpstream?.AbsoluteUri ?? "https://api.anthropic.com") with { AllowPrivateNetwork = testUpstream is not null };
        var snapshot = new UpstreamSnapshot(0, options, credential);
        Upstreams = new UpstreamPool(upstreams is null ? () => snapshot : upstreams.ReadActive, config.MaxConcurrent);
    }

    public WebApplication Build(X509Certificate2? certificate, int? testPort = null, X509Certificate2Collection? certificateChain = null)
    {
        if (certificate is null && testPort is null) throw new InvalidOperationException("正式服务必须启用 HTTPS。");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false; options.Limits.MaxConcurrentConnections = 64;
            options.Limits.MaxRequestBodySize = config.MaxBodyBytes;
            options.Limits.MaxRequestHeadersTotalSize = 16384;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
            options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
            void Configure(ListenOptions listen)
            {
                listen.Protocols = HttpProtocols.Http1AndHttp2;
                if (certificate is not null) listen.UseHttps(tls => { tls.ServerCertificate = certificate; tls.ServerCertificateChain = certificateChain; });
            }
            if (testPort is not null) options.Listen(IPAddress.Loopback, testPort.Value, Configure);
            else options.ListenAnyIP(config.ListenPort, Configure);
        });
        var app = builder.Build(); app.Run(Handle); return app;
    }

    internal static string? GetToken(HttpRequest request)
    {
        var authorization = request.Headers.Authorization.ToString();
        var bearer = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? authorization[7..] : null;
        var apiKey = request.Headers["x-api-key"].ToString();
        if (bearer is not null && apiKey.Length > 0 && bearer != apiKey) return null;
        return bearer ?? (apiKey.Length > 0 ? apiKey : null);
    }
    public async Task Handle(HttpContext context)
    {
        var stopwatch = Stopwatch.StartNew(); var id = Guid.NewGuid().ToString("N"); var name = "unknown";
        var path = context.Request.Path.Value ?? "/"; var outcome = "succeeded";
        UsageAccumulator? usage = null; var recorded = false; var dispatched = false; int? upstreamStatus = null;
        try
        {
            if (path == "/healthz" && context.Request.Method == "GET")
            { await context.Response.WriteAsJsonAsync(new { ok = true }, context.RequestAborted); return; }
            if (await PersonalUsage.TryHandle(context, Clients, Usage)) return;
            var token = GetToken(context.Request);
            var client = Clients.Authenticate(token) ?? throw new RelayFailure(401, "Invalid or disabled relay token.");
            name = client.Name;
            if (path == "/status" && context.Request.Method == "GET")
            {
                context.Response.Headers.CacheControl = "no-store";
                using var status = Upstreams.Acquire();
                await context.Response.WriteAsJsonAsync(new { client = name, active = Admission.Active, queued = Admission.Queued,
                    upstreamPaused = status.Paused, completed = Interlocked.Read(ref completed) }, context.RequestAborted); return;
            }
            if (path == "/api/hello" && context.Request.Method == "HEAD") { context.Response.StatusCode = 200; return; }
            if (!Routes.TryGetValue(path, out var method)) throw new RelayFailure(404, "Endpoint not exposed by this relay.");
            if (context.Request.Method != method) throw new RelayFailure(405, "Method not allowed.");
            var kind = path == "/v1/messages" ? "inference" : path == "/v1/models" ? "models" : "estimate";
            usage = new UsageAccumulator(kind);
            if (Usage is not null) { await Usage.Start(id, client, kind); recorded = true; }
            using var upstream = Upstreams.Acquire();
            upstream.CheckState(); Admission.CheckRate(client.Id);
            using var lease = await Admission.Enter(client.Id, context.RequestAborted);
            if (Clients.Authenticate(token) is null) throw new RelayFailure(401, "Relay token revoked while queued.");
            upstream.CheckState();
            var body = await ReadBody(context);
            if (Clients.Authenticate(token) is null) throw new RelayFailure(401, "Relay token revoked during upload.");
            upstream.CheckState();
            string? model = null;
            if (method == "POST")
            {
                if (!(context.Request.ContentType ?? "").StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
                    throw new RelayFailure(415, "Expected application/json.");
                try
                {
                    using var document = JsonDocument.Parse(body);
                    if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
                    if (document.RootElement.TryGetProperty("model", out var modelValue) && modelValue.ValueKind == JsonValueKind.String)
                        model = SafeModel(modelValue.GetString(), token, upstream.Snapshot.Credential);
                    if (upstream.Snapshot.Options.Mode == "oauth" && config.NormalizeDeviceMetadata) body = DeviceMetadata.Normalize(body, config.DeviceId);
                }
                catch (JsonException) { throw new RelayFailure(400, "Expected a JSON object."); }
            }
            // JSON 处理完成后再核验，缩短上传结束与真正发往上游之间的撤销窗口。
            if (Clients.Authenticate(token) is null) throw new RelayFailure(401, "Relay token revoked before dispatch.");
            using var request = upstream.Request(path, context.Request.QueryString.ToString(), body, method, config.UpstreamUserAgent, context.Request.Headers);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(config.RequestTimeoutSeconds));
            try
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (recorded) await Usage!.Dispatch(id, upstream.Snapshot.Version, model);
                dispatched = true;
                using var response = await upstream.Send(request, timeout.Token);
                upstreamStatus = (int)response.StatusCode;
                upstream.Observe(response);
                await UpstreamResponses.Copy(response, context.Response, upstream.Snapshot.Credential!, id, timeout.Token, usage);
                if (!response.IsSuccessStatusCode || usage.Error) outcome = "failed";
                else if (usage.IsStream && !usage.ProtocolComplete) outcome = "interrupted";
                Interlocked.Increment(ref completed);
            }
            catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
            { throw new RelayFailure(504, "Upstream request timed out."); }
            catch (HttpRequestException) { throw new RelayFailure(502, "Upstream connection failed. Check host connectivity."); }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { outcome = "cancelled"; context.Abort(); }
        catch (RelayFailure error) { outcome = "failed"; await Fail(context, error); }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException error) { outcome = "failed"; await Fail(context, new RelayFailure(error.StatusCode, "Invalid or oversized request.")); }
        catch (IOException) { outcome = "interrupted"; await Fail(context, new RelayFailure(502, "Upstream response interrupted.")); }
        catch (Exception) { outcome = "failed"; await Fail(context, new RelayFailure(500, "Internal relay error.")); }
        finally
        {
            if (recorded)
                await Usage!.Finish(id, new UsageEnd(outcome, dispatched, context.Response.StatusCode, upstreamStatus,
                    stopwatch.ElapsedMilliseconds, usage!.Completeness(dispatched, outcome == "succeeded"), usage.Tokens,
                    usage.ReportedMoney is { } money ? money with { Completeness = usage.ProtocolComplete && outcome == "succeeded" ? "complete" : "partial" } : null));
            log(new { time = DateTimeOffset.UtcNow, requestId = id, client = name, route = Routes.ContainsKey(path) ? path : "local",
                status = context.Response.StatusCode, outcome, durationMs = stopwatch.ElapsedMilliseconds });
        }
    }

    private static string? SafeModel(string? model, string? token, string? credential)
    {
        if (model is not { Length: > 0 and <= 160 } || model.Any(c => !(char.IsAsciiLetterOrDigit(c) || "-_.:/@".Contains(c)))) return null;
        if (token is { Length: > 0 } && model.Contains(token, StringComparison.Ordinal)
            || credential is { Length: > 0 } && model.Contains(credential, StringComparison.Ordinal)) return null;
        return model;
    }

    private async Task<byte[]> ReadBody(HttpContext context)
    {
        if (context.Request.ContentLength > config.MaxBodyBytes) throw new RelayFailure(413, "Request body exceeds relay limit.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(config.UploadTimeoutSeconds));
        using var memory = new MemoryStream(); var buffer = ArrayPool<byte>.Shared.Rent(32768);
        try
        {
            int count;
            while ((count = await context.Request.Body.ReadAsync(buffer, timeout.Token)) != 0)
            {
                if (memory.Length + count > config.MaxBodyBytes) throw new RelayFailure(413, "Request body exceeds relay limit.");
                memory.Write(buffer, 0, count);
            }
            return memory.ToArray();
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested) { throw new RelayFailure(408, "Request upload timed out."); }
        finally { ArrayPool<byte>.Shared.Return(buffer, true); }
    }

    private static async Task Fail(HttpContext context, RelayFailure error)
    {
        if (context.Response.HasStarted || context.RequestAborted.IsCancellationRequested) { context.Abort(); return; }
        context.Response.Clear(); context.Response.StatusCode = error.Status; context.Response.Headers.CacheControl = "no-store";
        // Rejected uploads may still have unread bytes; never reuse that HTTP/1 connection.
        if (context.Request.Protocol.StartsWith("HTTP/1", StringComparison.Ordinal)) context.Response.Headers.Connection = "close";
        if (error.RetryAfter > 0) context.Response.Headers.RetryAfter = error.RetryAfter.ToString();
        try { await context.Response.WriteAsJsonAsync(new { type = "error", error = new {
            type = error.Status == 429 ? "rate_limit_error" : error.Status == 401 ? "authentication_error" : "api_error", message = error.Message } }, context.RequestAborted); }
        catch (OperationCanceledException) { context.Abort(); }
    }
    public void Dispose() => Upstreams.Dispose();
}
