using System.Net;
using System.Net.Http.Headers;

namespace HouseholdRelay;

/// <summary>同一上游版本的连接和运行状态；引用释放后才销毁客户端。</summary>
public sealed class UpstreamConnection : IDisposable
{
    private static readonly HashSet<string> PrivateHeaders = new(StringComparer.OrdinalIgnoreCase)
    { "Authorization", "X-Api-Key", "Cookie", "Host", "Forwarded", "X-Real-IP", "Content-Length", "Accept-Encoding", "User-Agent",
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization", "TE", "Trailer", "Transfer-Encoding", "Upgrade", "anthropic-beta" };
    private readonly HttpClient http;
    private long cooldownUntil;
    private int paused, references = 1;
    public UpstreamSnapshot Snapshot { get; }
    public bool Paused => Volatile.Read(ref paused) != 0;
    public int CooldownSeconds => (int)Math.Max(0, Math.Ceiling((Interlocked.Read(ref cooldownUntil) - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) / 1000d));

    public UpstreamConnection(UpstreamSnapshot snapshot, int maxConnections = 2)
    {
        Snapshot = snapshot;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseProxy = false, AutomaticDecompression = DecompressionMethods.None,
            MaxConnectionsPerServer = maxConnections, PooledConnectionLifetime = TimeSpan.FromMinutes(2), ConnectTimeout = TimeSpan.FromSeconds(15),
            ConnectCallback = (context, cancellation) => UpstreamAddress.Connect(context, snapshot.Options, cancellation)
        };
        http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }
    internal UpstreamConnection Retain() { Interlocked.Increment(ref references); return this; }
    public void CheckState()
    {
        if (!Snapshot.Configured) throw new RelayFailure(503, "Upstream is not configured.");
        if (Paused) throw new RelayFailure(503, "Upstream authentication paused. Test and activate a new credential in local management.");
        var seconds = CooldownSeconds;
        if (seconds > 0) throw new RelayFailure(429, "Upstream cooldown is active.", seconds);
    }
    internal HttpRequestMessage Request(string route, string query, byte[] body, string method, string userAgent, IHeaderDictionary? headers = null)
    {
        var options = Snapshot.Options;
        var request = new HttpRequestMessage(new HttpMethod(method), UpstreamAddress.Endpoint(options, route, query))
        { Content = new ByteArrayContent(body), Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionOrLower };
        var connectionHeaders = (headers?.Connection.ToString() ?? "").Split(',').Select(value => value.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (headers is not null)
            foreach (var header in headers)
            {
                if (PrivateHeaders.Contains(header.Key) || connectionHeaders.Contains(header.Key)
                    || header.Key.StartsWith("x-forwarded-", StringComparison.OrdinalIgnoreCase) || header.Key.StartsWith("x-stainless-", StringComparison.OrdinalIgnoreCase)) continue;
                if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray())) request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
        // API 上游可能校验客户端请求特征；账户模式仍统一使用部署配置的身份。
        var clientAgent = headers?["User-Agent"].ToString();
        var forwardedAgent = options.Mode == "api-key" && !connectionHeaders.Contains("User-Agent")
            && clientAgent is { Length: > 0 and <= 256 } && !clientAgent.Any(char.IsControl) ? clientAgent : userAgent;
        request.Headers.TryAddWithoutValidation("User-Agent", forwardedAgent); request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        if (!request.Headers.Contains("anthropic-version")) request.Headers.Add("anthropic-version", "2023-06-01");
        var betas = (headers?["anthropic-beta"].ToString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => options.Mode == "oauth" && options.OAuthBeta || !value.StartsWith("oauth-", StringComparison.OrdinalIgnoreCase));
        if (options.Mode == "oauth" && options.OAuthBeta) betas = betas.Append("oauth-2025-04-20");
        var beta = string.Join(',', betas.Distinct());
        if (beta.Length != 0) request.Headers.TryAddWithoutValidation("anthropic-beta", beta);
        if (options.Authentication == "bearer") request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Snapshot.Credential);
        else request.Headers.TryAddWithoutValidation("x-api-key", Snapshot.Credential);
        return request;
    }
    internal async Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken cancellation)
    {
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        if ((int)response.StatusCode is >= 300 and < 400) { response.Dispose(); throw new RelayFailure(502, "Upstream redirect rejected."); }
        return response;
    }
    internal void Observe(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) Interlocked.Exchange(ref paused, 1);
        if (response.StatusCode != HttpStatusCode.TooManyRequests) return;
        var retry = response.Headers.RetryAfter;
        var seconds = retry?.Delta?.TotalSeconds ?? (retry?.Date - DateTimeOffset.UtcNow)?.TotalSeconds ?? 30;
        var until = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(seconds, 1, 3600)).ToUnixTimeMilliseconds();
        long previous;
        do { previous = Interlocked.Read(ref cooldownUntil); if (previous >= until) return; }
        while (Interlocked.CompareExchange(ref cooldownUntil, until, previous) != previous);
    }
    public void Dispose() { if (Interlocked.Decrement(ref references) == 0) http.Dispose(); }
}

/// <summary>按数据库活动版本更换连接，旧请求的租约继续使用原地址、凭据和状态。</summary>
public sealed class UpstreamPool(Func<UpstreamSnapshot> read, int maxConnections) : IDisposable
{
    private readonly object sync = new();
    private UpstreamConnection? current;
    public UpstreamConnection Acquire()
    {
        lock (sync)
        {
            var snapshot = read();
            if (current is null || current.Snapshot.Version != snapshot.Version)
            {
                var replacement = new UpstreamConnection(snapshot, maxConnections);
                var previous = current; current = replacement; previous?.Dispose();
            }
            return current.Retain();
        }
    }
    public (bool Paused, int CooldownSeconds) Status(long version)
    { lock (sync) return current?.Snapshot.Version == version ? (current.Paused, current.CooldownSeconds) : (false, 0); }
    public void Dispose() { lock (sync) { current?.Dispose(); current = null; } }
}
