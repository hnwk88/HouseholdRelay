using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace HouseholdRelay;

public sealed class RelayServer : IDisposable
{
    private static readonly HashSet<string> HopHeaders = new(StringComparer.OrdinalIgnoreCase)
    { "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization", "TE", "Trailer", "Transfer-Encoding", "Upgrade" };
    private static readonly HashSet<string> PrivateHeaders = new(StringComparer.OrdinalIgnoreCase)
    { "Authorization", "X-Api-Key", "Cookie", "Host", "Forwarded", "X-Real-IP", "Content-Length", "Accept-Encoding", "User-Agent" };
    private static readonly Dictionary<string, string> Routes = new()
    { ["/v1/messages"] = "POST", ["/v1/messages/count_tokens"] = "POST", ["/v1/models"] = "GET" };
    private readonly RelayConfiguration config;
    private readonly string credential;
    private readonly HttpClient http;
    private readonly Uri upstream;
    private readonly Action<object> log;
    private long cooldownUntil, completed;
    private int paused;
    public ClientRegistry Clients { get; }
    public Admission Admission { get; }

    public RelayServer(RelayConfiguration config, string credential, Action<object>? log = null, Uri? testUpstream = null)
    {
        config.Validate();
        if (string.IsNullOrWhiteSpace(credential) || credential.Any(char.IsControl)) throw new InvalidOperationException("账号令牌格式错误。");
        if (testUpstream is not null && !(testUpstream.Scheme == "http" && testUpstream.Host == "127.0.0.1"))
            throw new InvalidOperationException("模拟上游只允许本机 HTTP。");
        this.config = config; this.credential = credential; this.log = log ?? (_ => { });
        upstream = testUpstream ?? new Uri("https://api.anthropic.com");
        Clients = new ClientRegistry(config.Clients); Admission = new Admission(config);
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.None,
            MaxConnectionsPerServer = config.MaxConcurrent, PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(15), UseProxy = testUpstream is null && config.UpstreamProxy is not null,
            Proxy = config.UpstreamProxy is null ? null : new WebProxy(config.UpstreamProxy)
        };
        http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public WebApplication Build(X509Certificate2? certificate, int? testPort = null)
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
                if (certificate is not null) listen.UseHttps(certificate);
            }
            if (testPort is not null) options.Listen(IPAddress.Loopback, testPort.Value, Configure);
            else options.ListenAnyIP(config.ListenPort, Configure);
        });
        var app = builder.Build(); app.Run(Handle); return app;
    }

    private static string? GetToken(HttpRequest request)
    {
        var authorization = request.Headers.Authorization.ToString();
        var bearer = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? authorization[7..] : null;
        var apiKey = request.Headers["x-api-key"].ToString();
        if (bearer is not null && apiKey.Length > 0 && bearer != apiKey) return null;
        return bearer ?? (apiKey.Length > 0 ? apiKey : null);
    }
    private void CheckState()
    {
        if (Volatile.Read(ref paused) != 0) throw new RelayFailure(503, "Upstream authentication paused. Update the host credential and restart.");
        var remaining = Interlocked.Read(ref cooldownUntil) - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (remaining > 0) throw new RelayFailure(429, "Upstream cooldown is active.", (int)Math.Ceiling(remaining / 1000d));
    }
    private async Task Handle(HttpContext context)
    {
        var stopwatch = Stopwatch.StartNew(); var id = Guid.NewGuid().ToString("N"); var name = "unknown";
        var path = context.Request.Path.Value ?? "/"; var outcome = "complete";
        try
        {
            if (path == "/healthz" && context.Request.Method == "GET")
            { await context.Response.WriteAsJsonAsync(new { ok = true }, context.RequestAborted); return; }
            var token = GetToken(context.Request);
            var client = Clients.Authenticate(token) ?? throw new RelayFailure(401, "Invalid or disabled relay token.");
            name = client.Name;
            if (path == "/status" && context.Request.Method == "GET")
            {
                context.Response.Headers.CacheControl = "no-store";
                await context.Response.WriteAsJsonAsync(new { client = name, active = Admission.Active, queued = Admission.Queued,
                    upstreamPaused = Volatile.Read(ref paused) != 0, completed = Interlocked.Read(ref completed) }, context.RequestAborted); return;
            }
            if (path == "/api/hello" && context.Request.Method == "HEAD") { context.Response.StatusCode = 200; return; }
            if (!Routes.TryGetValue(path, out var method)) throw new RelayFailure(404, "Endpoint not exposed by this relay.");
            if (context.Request.Method != method) throw new RelayFailure(405, "Method not allowed.");
            CheckState(); Admission.CheckRate(name);
            using var lease = await Admission.Enter(name, context.RequestAborted);
            if (Clients.Authenticate(token) is null) throw new RelayFailure(401, "Relay token revoked while queued.");
            CheckState();
            var body = await ReadBody(context);
            if (Clients.Authenticate(token) is null) throw new RelayFailure(401, "Relay token revoked during upload.");
            CheckState();
            if (method == "POST")
            {
                if (!(context.Request.ContentType ?? "").StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
                    throw new RelayFailure(415, "Expected application/json.");
                try
                {
                    using var document = JsonDocument.Parse(body);
                    if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
                    if (config.NormalizeDeviceMetadata) body = DeviceMetadata.Normalize(body, config.DeviceId);
                }
                catch (JsonException) { throw new RelayFailure(400, "Expected a JSON object."); }
            }
            using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(upstream, path + context.Request.QueryString))
            {
                Content = new ByteArrayContent(body), Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
            };
            var connectionHeaders = context.Request.Headers.Connection.ToString().Split(',').Select(s => s.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var header in context.Request.Headers)
            {
                // Transport identity is supplied by this host. Semantic headers remain open-ended.
                if (HopHeaders.Contains(header.Key) || PrivateHeaders.Contains(header.Key) || connectionHeaders.Contains(header.Key)
                    || header.Key.StartsWith("x-forwarded-", StringComparison.OrdinalIgnoreCase)
                    || header.Key.StartsWith("x-stainless-", StringComparison.OrdinalIgnoreCase)) continue;
                if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray())) request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
            request.Headers.TryAddWithoutValidation("User-Agent", config.UpstreamUserAgent);
            request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
            if (!request.Headers.Contains("anthropic-version")) request.Headers.Add("anthropic-version", "2023-06-01");
            if (config.AuthMode == "oauth")
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
                var betas = context.Request.Headers["anthropic-beta"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Append("oauth-2025-04-20").Distinct();
                request.Headers.Remove("anthropic-beta"); request.Headers.TryAddWithoutValidation("anthropic-beta", string.Join(',', betas));
            }
            else request.Headers.TryAddWithoutValidation("x-api-key", credential);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(config.RequestTimeoutSeconds));
            try
            {
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if ((int)response.StatusCode is >= 300 and < 400) throw new RelayFailure(502, "Upstream redirect rejected.");
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) Interlocked.Exchange(ref paused, 1);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var retry = response.Headers.RetryAfter;
                    var seconds = retry?.Delta?.TotalSeconds ?? (retry?.Date - DateTimeOffset.UtcNow)?.TotalSeconds ?? 30;
                    var until = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(seconds, 1, 3600)).ToUnixTimeMilliseconds();
                    long previous;
                    do { previous = Interlocked.Read(ref cooldownUntil); if (previous >= until) break; }
                    while (Interlocked.CompareExchange(ref cooldownUntil, until, previous) != previous);
                }
                context.Response.StatusCode = (int)response.StatusCode;
                var excluded = response.Headers.Connection.ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var header in response.Headers.Concat(response.Content.Headers))
                    if (!HopHeaders.Contains(header.Key) && !excluded.Contains(header.Key) && !header.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
                        context.Response.Headers[header.Key] = header.Value.ToArray();
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers["X-Accel-Buffering"] = "no"; context.Response.Headers["X-Relay-Request-Id"] = id;
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                var buffer = ArrayPool<byte>.Shared.Rent(32768);
                try
                {
                    int count;
                    while ((count = await stream.ReadAsync(buffer, timeout.Token)) != 0)
                    { await context.Response.Body.WriteAsync(buffer.AsMemory(0, count), timeout.Token); await context.Response.Body.FlushAsync(timeout.Token); }
                }
                finally { ArrayPool<byte>.Shared.Return(buffer, true); }
                Interlocked.Increment(ref completed);
            }
            catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
            { throw new RelayFailure(504, "Upstream request timed out."); }
            catch (HttpRequestException) { throw new RelayFailure(502, "Upstream connection failed. Check host connectivity."); }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { outcome = "cancelled"; context.Abort(); }
        catch (RelayFailure error) { outcome = "error"; await Fail(context, error); }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException error) { outcome = "error"; await Fail(context, new RelayFailure(error.StatusCode, "Invalid or oversized request.")); }
        catch (Exception) { outcome = "error"; await Fail(context, new RelayFailure(500, "Internal relay error.")); }
        finally
        {
            log(new { time = DateTimeOffset.UtcNow, requestId = id, client = name, route = Routes.ContainsKey(path) ? path : "local",
                status = context.Response.StatusCode, outcome, durationMs = stopwatch.ElapsedMilliseconds });
        }
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
    public void Dispose() => http.Dispose();
}
