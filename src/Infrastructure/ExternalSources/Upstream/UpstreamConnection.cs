using System.Net;
using System.Net.Http.Headers;
using Application.Interfaces.Upstream;
using Core.Relay;
using Core.Upstream;
using Microsoft.Extensions.Primitives;

namespace Infrastructure.ExternalSources.Upstream;

/// <summary>同一上游版本的连接和运行状态；引用释放后才销毁客户端。</summary>
internal sealed class UpstreamConnection : IUpstreamLease
{
    private static readonly HashSet<string> PrivateHeaders = new(StringComparer.OrdinalIgnoreCase)
    { "Authorization", "X-Api-Key", "Cookie", "Host", "Forwarded", "X-Real-IP", "Content-Length", "Accept-Encoding", "User-Agent",
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization", "TE", "Trailer", "Transfer-Encoding", "Upgrade", "anthropic-beta" };
    private readonly HttpClient http;
    private long cooldownUntil;
    private int paused, references = 1;
    private UpstreamAvoidance? avoidance;
    /// <summary>创建本连接时读到的上游版本快照（地址、凭据、模式等）；整个连接生命周期内不变，因此旧租约在版本切换后仍按原配置发请求。</summary>
    public UpstreamSnapshot Snapshot { get; }
    /// <summary>上游曾返回 401 或 403 时置为真，表示凭据已失效需在本机管理页重新测试激活；用易失读取，多线程下立即可见。</summary>
    public bool Paused => Volatile.Read(ref paused) != 0;
    /// <summary>距离上游 429 冷却结束还剩多少秒（向上取整，最小为 0）；按原子读取的截止毫秒数与当前时间计算。</summary>
    public int CooldownSeconds => (int)Math.Max(0, Math.Ceiling((Interlocked.Read(ref cooldownUntil) - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) / 1000d));

    /// <summary>为一个上游版本建立专用 HTTP 客户端：不跟随重定向、不用 Cookie、不自动解压，限制每服务器连接数；处理器自身不用系统代理，建连交给 <see cref="UpstreamDialer"/> 按全局代理设置与地址规则完成；客户端总超时关闭，由调用方的取消令牌控制请求时长。初始引用计数为 1，归连接池持有。</summary>
    public UpstreamConnection(UpstreamSnapshot snapshot, IUpstreamProxy proxy, int maxConnections = 2)
    {
        Snapshot = snapshot;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseProxy = false, AutomaticDecompression = DecompressionMethods.None,
            MaxConnectionsPerServer = maxConnections, PooledConnectionLifetime = TimeSpan.FromMinutes(2), ConnectTimeout = TimeSpan.FromSeconds(15),
            ConnectCallback = (context, cancellation) => UpstreamDialer.Connect(context, snapshot.Options, proxy, cancellation)
        };
        http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }
    /// <summary>原子地增加一次引用并返回自身，供连接池在锁内发放租约；每个租约对应一次 <see cref="Dispose"/>。</summary>
    internal UpstreamConnection Retain() { Interlocked.Increment(ref references); return this; }
    /// <summary>发请求前检查上游是否可用：未配置或认证已暂停时抛 503，冷却期内抛 429 并带剩余秒数，让中转直接拒绝而不去消耗上游额度。</summary>
    public void CheckState()
    {
        if (!Snapshot.Configured) throw new RelayFailure(503, "Upstream is not configured.");
        if (Paused) throw new RelayFailure(503, "Upstream authentication paused. Test and activate a new credential in local management.");
        var seconds = CooldownSeconds;
        if (seconds > 0) throw new RelayFailure(429, "Upstream cooldown is active.", seconds);
    }
    /// <summary>按客户端请求组装发往上游的请求：剔除认证、Cookie、逐跳、转发类和 x-stainless 等私有请求头，仅 api-key 模式转发客户端 User-Agent，补齐 anthropic-version，按 OAuth 设置过滤或追加 anthropic-beta，最后写入本版本的上游凭据。返回的请求由调用方释放。</summary>
    public HttpRequestMessage Request(string route, string query, byte[] body, string method, string userAgent, IDictionary<string, StringValues>? headers = null)
    {
        var options = Snapshot.Options;
        var request = new HttpRequestMessage(new HttpMethod(method), UpstreamAddress.Endpoint(options, route, query))
        { Content = new ByteArrayContent(body), Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionOrLower };
        var connectionHeaders = (headers is null ? "" : Header(headers, "Connection").ToString()).Split(',').Select(value => value.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (headers is not null)
            foreach (var header in headers)
            {
                if (PrivateHeaders.Contains(header.Key) || connectionHeaders.Contains(header.Key)
                    || header.Key.StartsWith("x-forwarded-", StringComparison.OrdinalIgnoreCase) || header.Key.StartsWith("x-stainless-", StringComparison.OrdinalIgnoreCase)) continue;
                if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray())) request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
        // API 上游可能校验客户端请求特征；账户模式仍统一使用部署配置的身份。
        var clientAgent = headers is null ? null : Header(headers, "User-Agent").ToString();
        var forwardedAgent = options.Mode == "api-key" && !connectionHeaders.Contains("User-Agent")
            && clientAgent is { Length: > 0 and <= 256 } && !clientAgent.Any(char.IsControl) ? clientAgent : userAgent;
        request.Headers.TryAddWithoutValidation("User-Agent", forwardedAgent); request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        if (!request.Headers.Contains("anthropic-version")) request.Headers.Add("anthropic-version", "2023-06-01");
        var betas = (headers is null ? "" : Header(headers, "anthropic-beta").ToString()).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => options.Mode == "oauth" && options.OAuthBeta || !value.StartsWith("oauth-", StringComparison.OrdinalIgnoreCase));
        if (options.Mode == "oauth" && options.OAuthBeta) betas = betas.Append("oauth-2025-04-20");
        var beta = string.Join(',', betas.Distinct());
        if (beta.Length != 0) request.Headers.TryAddWithoutValidation("anthropic-beta", beta);
        if (options.Authentication == "bearer") request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Snapshot.Credential);
        else request.Headers.TryAddWithoutValidation("x-api-key", Snapshot.Credential);
        return request;
    }
    /// <summary>发送请求，读到响应头即返回以便流式转发；上游返回 3xx 时释放响应并抛 502，拒绝被重定向到未校验的地址。返回的响应由调用方释放。</summary>
    public async Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken cancellation)
    {
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        if ((int)response.StatusCode is >= 300 and < 400) { response.Dispose(); throw new RelayFailure(502, "Upstream redirect rejected."); }
        return response;
    }
    /// <summary>根据上游响应更新本版本状态：401、403 标记认证暂停；429 按 Retry-After 设置冷却（缺省 30 秒，限 1 到 3600 秒），用比较交换循环只把截止时间往后推，多个并发响应互不覆盖。</summary>
    public void Observe(HttpResponseMessage response)
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
    /// <summary>自动切换把这家换掉时记下暂避：到期时刻与最后一次失败的读法；后记的覆盖先记的。</summary>
    internal void Avoid(UpstreamAvoidance value) => Volatile.Write(ref avoidance, value);

    /// <summary>在 <paramref name="now"/> 这一刻是否还在暂避中。</summary>
    internal bool Avoided(DateTimeOffset now) => Volatile.Read(ref avoidance) is { Until: { } until } && until > now;

    /// <summary>
    /// 这家此刻为什么排不到前面，供页面读出：认证暂停优先，其次 429 冷却，再次暂避；都没有时为 null。
    /// <paramref name="now"/> 只用于暂避到期判断，冷却沿用连接自己按系统时钟记的截止时刻。
    /// </summary>
    internal UpstreamAvoidance? Held(DateTimeOffset now)
    {
        if (Paused) return new(Snapshot.CandidateId, "paused", null, null, null, null);
        if (CooldownSeconds > 0) return new(Snapshot.CandidateId, "cooldown", DateTimeOffset.FromUnixTimeMilliseconds(Interlocked.Read(ref cooldownUntil)), 429, null, null);
        return Avoided(now) ? Volatile.Read(ref avoidance) : null;
    }

    /// <summary>这家现在能不能接请求：有凭据、没有认证暂停、不在 429 冷却中；暂避不算，暂避的只是排到最后。</summary>
    internal bool Usable => Snapshot.Configured && !Paused && CooldownSeconds == 0;

    /// <summary>归还一次引用；最后一个持有者（连接池或在途请求的租约）归还时才销毁 HTTP 客户端，保证版本切换不会打断仍在进行的请求。</summary>
    public void Dispose() { if (Interlocked.Decrement(ref references) == 0) http.Dispose(); }

    /// <summary>从请求头字典（Kestrel 的请求头集合）按名称大小写不敏感查找；缺失时与空值等同。</summary>
    private static StringValues Header(IDictionary<string, StringValues> headers, string name) =>
        headers.TryGetValue(name, out var value) ? value : StringValues.Empty;
}
