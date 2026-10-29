using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Application.Interfaces.Relay;
using Application.Interfaces.Upstream;
using Application.Interfaces.Usage;
using Core.Configuration;
using Core.Identity;
using Core.Relay;
using Core.Usage;
using Infrastructure.ExternalSources.Upstream;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace HouseholdRelay.Relay;

/// <summary>中转端点：鉴权、准入、用量记录、请求改写与上游转发；依赖全部由容器注入。</summary>
public sealed class RelayServer : IDisposable
{
    private static readonly Dictionary<string, string> Routes = new()
    { ["/v1/messages"] = "POST", ["/v1/messages/count_tokens"] = "POST", ["/v1/models"] = "GET" };
    private readonly RelayConfiguration config;
    private readonly IRelayEventLog log;
    private long completed;
    /// <summary>个人令牌鉴权名单；每个请求在入口、排队后、上传后和发往上游前都会重新核验，令牌被停用或轮换后尽快拒绝。</summary>
    public ClientRegistry Clients { get; }
    /// <summary>准入器：执行每客户端每分钟限速、单客户端与全家并发上限及排队上限。</summary>
    public Admission Admission { get; }
    /// <summary>按活动上游版本提供连接租约的上游池；进程释放时随本对象一起释放。</summary>
    public IUpstreamPool Upstreams { get; }
    /// <summary>对外 HTTPS 中转宿主是否已在本进程启动；只由 serve 命令在启动成功后置为真，管理页据此提示新证书是否需要重启。</summary>
    public bool HttpsActive { get; internal set; }
    /// <summary>用量服务；为空时不记录用量，个人用量数据接口返回 503。</summary>
    public IUsageService? Usage { get; }

    /// <summary>用量服务可选：独立中转模式不登记用量服务，此时不记录用量，个人用量入口返回 503。</summary>
    public RelayServer(RelayConfiguration config, ClientRegistry clients, Admission admission, IUpstreamPool upstreams, IRelayEventLog log, IUsageService? usage = null)
    {
        config.Validate();
        this.config = config; this.log = log;
        Clients = clients; Admission = admission; Upstreams = upstreams; Usage = usage;
    }

    /// <summary>构建对外中转宿主（只构建不启动，由调用方启动）：正式服务必须提供证书并监听所有网卡上的 <see cref="RelayConfiguration.ListenPort"/>；只有测试可传 <paramref name="testPort"/> 改为监听本机回环且允许不启用 HTTPS。限制请求体、请求头大小与超时，所有请求交给 <see cref="Handle"/>。</summary>
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

    /// <summary>从 Authorization: Bearer 或 x-api-key 头取个人中转令牌；两个头同时出现且值不同时返回空，按无令牌处理，避免含糊的身份。中转入口和个人用量入口共用。</summary>
    internal static string? GetToken(HttpRequest request)
    {
        var authorization = request.Headers.Authorization.ToString();
        var bearer = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? authorization[7..] : null;
        var apiKey = request.Headers["x-api-key"].ToString();
        if (bearer is not null && apiKey.Length > 0 && bearer != apiKey) return null;
        return bearer ?? (apiKey.Length > 0 ? apiKey : null);
    }
    /// <summary>中转请求处理入口，对外宿主直接使用，管理宿主也把未匹配页面的请求兜底转到这里：/healthz 无需令牌；个人用量入口先行处理；其余请求须带有效令牌，只开放 /status、/api/hello 与三个 /v1 路由。流程为记录用量开始、限速与排队准入、限长读取请求体、校验 JSON 并按需改写设备元数据、记录发送、转发并流式回传，期间多次重新核验令牌。无论成败都在 finally 中写用量结束结果和一行不含令牌的事件日志。</summary>
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
                // 请求体不是 JSON 对象时在发往上游之前就以 400 拒绝，不把解析器的内部消息透给客户端。
                catch (JsonException) { throw new RelayFailure(400, "Expected a JSON object."); }
            }
            // JSON 处理完成后再核验，缩短上传结束与真正发往上游之间的撤销窗口。
            if (Clients.Authenticate(token) is null) throw new RelayFailure(401, "Relay token revoked before dispatch.");
            using var request = upstream.Request(path, context.Request.QueryString.ToString(), body, method, config.UpstreamUserAgent, context.Request.Headers);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            // 总超时与客户端断开共用同一个令牌：客户端断开时立即取消上游调用，超时时也会取消，二者在下方 catch 里按 RequestAborted 区分。
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
            // 客户端仍在连接却被取消，只能是总超时触发，转成 504；客户端自己断开的情况留给外层按 cancelled 处理。
            catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
            { throw new RelayFailure(504, "Upstream request timed out."); }
            // 代理已启用却取不到地址：建连前就拒绝，请求没有发出，用量记成未发出的失败（发送标记在此之前已写，结束记录会按这里改回）；
            // 固定提示让使用者知道去找管理员查代理，不透出代理地址。
            catch (HttpRequestException error) when (ProxyUnavailable(error))
            { dispatched = false; throw new RelayFailure(502, "Upstream proxy is enabled but unavailable; the request was not sent. Ask the administrator to check proxy settings in local management."); }
            // 连不上上游时只回固定的 502 提示，不透出上游地址或底层网络错误细节。
            catch (HttpRequestException) { throw new RelayFailure(502, "Upstream connection failed. Check host connectivity."); }
        }
        // 客户端主动断开：记为 cancelled 并直接中止连接，不再尝试写错误响应。
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { outcome = "cancelled"; context.Abort(); }
        // 鉴权、限速、准入、校验等可预期的拒绝，按其状态码和固定消息写回。
        catch (RelayFailure error) { outcome = "failed"; await Fail(context, error); }
        // Kestrel 读取请求时发现格式错误或超限，沿用其状态码，但消息换成固定文本。
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException error) { outcome = "failed"; await Fail(context, new RelayFailure(error.StatusCode, "Invalid or oversized request.")); }
        // 回传上游响应途中流被打断，记为 interrupted；若已开始写响应，Fail 会直接中止连接。
        catch (IOException) { outcome = "interrupted"; await Fail(context, new RelayFailure(502, "Upstream response interrupted.")); }
        // 兜底：未预料的异常只回 500 固定消息，不泄露堆栈、令牌或上游凭据。
        catch (Exception) { outcome = "failed"; await Fail(context, new RelayFailure(500, "Internal relay error.")); }
        // 无论成败都要写用量结束结果和事件日志，保证已开始的用量记录不会悬空；日志只含请求编号、客户端名和路由，不含令牌。
        finally
        {
            if (recorded)
                await Usage!.Finish(id, new UsageEnd(outcome, dispatched, context.Response.StatusCode, upstreamStatus,
                    stopwatch.ElapsedMilliseconds, usage!.Completeness(dispatched, outcome == "succeeded"), usage.Tokens,
                    usage.ReportedMoney is { } money ? money with { Completeness = usage.ProtocolComplete && outcome == "succeeded" ? "complete" : "partial" } : null));
            log.Write(new { time = DateTimeOffset.UtcNow, requestId = id, client = name, route = Routes.ContainsKey(path) ? path : "local",
                status = context.Response.StatusCode, outcome, durationMs = stopwatch.ElapsedMilliseconds });
        }
    }

    /// <summary>建连异常链里是否有「代理已启用却取不到地址」；有就说明请求根本没有发出。</summary>
    private static bool ProxyUnavailable(Exception error)
    {
        for (Exception? inner = error; inner is not null; inner = inner.InnerException) if (inner is Core.Upstream.UpstreamProxyUnavailableException) return true;
        return false;
    }

    /// <summary>把请求体里的模型名过滤成可安全写入用量记录的值：长度超过 160、含白名单以外字符，或包含个人令牌、上游凭据时一律记为空，防止秘密借模型字段落库。</summary>
    private static string? SafeModel(string? model, string? token, string? credential)
    {
        if (model is not { Length: > 0 and <= 160 } || model.Any(c => !(char.IsAsciiLetterOrDigit(c) || "-_.:/@".Contains(c)))) return null;
        if (token is { Length: > 0 } && model.Contains(token, StringComparison.Ordinal)
            || credential is { Length: > 0 } && model.Contains(credential, StringComparison.Ordinal)) return null;
        return model;
    }

    /// <summary>在 <see cref="RelayConfiguration.UploadTimeoutSeconds"/> 内把请求体读入内存：声明长度或实际累计超过 <see cref="RelayConfiguration.MaxBodyBytes"/> 时返回 413，上传超时返回 408；读缓冲取自共享池，用完清零归还。</summary>
    private async Task<byte[]> ReadBody(HttpContext context)
    {
        if (context.Request.ContentLength > config.MaxBodyBytes) throw new RelayFailure(413, "Request body exceeds relay limit.");
        // 与客户端断开联动：断开时立即停止读取，不再占用连接与内存。
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        // 上传限时，防止慢速上传长期占着已取得的并发名额。
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
        // 客户端仍在连接却被取消，说明是上传超时，转成 408；客户端断开的取消原样上抛，由外层记为 cancelled。
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested) { throw new RelayFailure(408, "Request upload timed out."); }
        // 缓冲区里可能残留请求内容（含提示词等），归还共享池前先清零，避免被其他请求读到。
        finally { ArrayPool<byte>.Shared.Return(buffer, true); }
    }

    /// <summary>按 Anthropic 错误格式写出中转失败响应，只含状态码对应的类型和固定消息，不含内部细节；响应已开始或客户端已断开时直接中止连接。</summary>
    private static async Task Fail(HttpContext context, RelayFailure error)
    {
        if (context.Response.HasStarted || context.RequestAborted.IsCancellationRequested) { context.Abort(); return; }
        context.Response.Clear(); context.Response.StatusCode = error.Status; context.Response.Headers.CacheControl = "no-store";
        // Rejected uploads may still have unread bytes; never reuse that HTTP/1 connection.
        if (context.Request.Protocol.StartsWith("HTTP/1", StringComparison.Ordinal)) context.Response.Headers.Connection = "close";
        if (error.RetryAfter > 0) context.Response.Headers.RetryAfter = error.RetryAfter.ToString();
        try { await context.Response.WriteAsJsonAsync(new { type = "error", error = new {
            type = error.Status == 429 ? "rate_limit_error" : error.Status == 401 ? "authentication_error" : "api_error", message = error.Message } }, context.RequestAborted); }
        // 写错误响应时客户端已断开，没必要再写，直接中止连接收场。
        catch (OperationCanceledException) { context.Abort(); }
    }
    /// <summary>释放上游池持有的连接；随容器释放本对象时调用。</summary>
    public void Dispose() => Upstreams.Dispose();
}
