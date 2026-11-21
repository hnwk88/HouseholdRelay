using Application.Interfaces.Relay;
using Core.Relay;
using Microsoft.Extensions.Primitives;

namespace HouseholdRelay.Relay;

/// <summary>把一次 ASP.NET Core 请求包成转发服务认识的交换端口：请求字段原样转交，读请求体时 Kestrel 的格式错误换成中转失败，错误按 Anthropic 格式写回。</summary>
internal sealed class HttpRelayExchange(HttpContext context) : IRelayExchange, IRelayReply
{
    private IReadOnlyDictionary<string, StringValues>? query;

    /// <summary>请求路径；没有时为「/」。</summary>
    public string Path => context.Request.Path.Value ?? "/";

    /// <summary>请求方法。</summary>
    public string Method => context.Request.Method;

    /// <summary>原样的查询串。</summary>
    public string QueryString => context.Request.QueryString.ToString();

    /// <summary>请求的 Content-Type。</summary>
    public string? ContentType => context.Request.ContentType;

    /// <summary>请求声明的正文长度。</summary>
    public long? ContentLength => context.Request.ContentLength;

    /// <summary>全部请求头。</summary>
    public IDictionary<string, StringValues> Headers => context.Request.Headers;

    /// <summary>查询参数，第一次用到时才整理（键不区分大小写）。</summary>
    public IReadOnlyDictionary<string, StringValues> Query =>
        query ??= context.Request.Query.ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);

    /// <summary>客户端断开时取消。</summary>
    public CancellationToken Aborted => context.RequestAborted;

    /// <summary>回话出口就是本对象。</summary>
    public IRelayReply Reply => this;

    /// <summary>取一个请求头的值。</summary>
    public StringValues Header(string name) => context.Request.Headers[name];

    /// <summary>读一段请求体；Kestrel 读请求时发现格式错误或超限，沿用其状态码，消息换成固定文本。</summary>
    public async ValueTask<int> Read(Memory<byte> buffer, CancellationToken cancellation)
    {
        try { return await context.Request.Body.ReadAsync(buffer, cancellation); }
        // 只接 Kestrel 的请求格式错误或超限：状态码照用，消息不透出解析细节；其余异常（含取消）原样上抛，由转发服务分类。
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException error) { throw new RelayFailure(error.StatusCode, "Invalid or oversized request."); }
    }

    /// <summary>按 Anthropic 错误格式写出中转失败响应，只含状态码对应的类型和固定消息，不含内部细节；响应已开始或客户端已断开时直接中止连接。</summary>
    public async Task Fail(RelayFailure error)
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

    /// <summary>响应状态码。</summary>
    public int StatusCode { get => context.Response.StatusCode; set => context.Response.StatusCode = value; }

    /// <summary>响应是否已开始写出。</summary>
    public bool HasStarted => context.Response.HasStarted;

    /// <summary>响应的 Content-Type。</summary>
    string? IRelayReply.ContentType { get => context.Response.ContentType; set => context.Response.ContentType = value; }

    /// <summary>响应正文流。</summary>
    public Stream Body => context.Response.Body;

    /// <summary>设置一个响应头，同名覆盖。</summary>
    public void SetHeader(string name, string[] values) => context.Response.Headers[name] = values;

    /// <summary>按值的实际类型序列化成 JSON 写出，与直接对该类型调用 WriteAsJsonAsync 的输出相同。</summary>
    public Task WriteJson(object value, CancellationToken cancellation) => context.Response.WriteAsJsonAsync(value, value.GetType(), cancellation);

    /// <summary>原样写出一个文件。</summary>
    public Task SendFile(string path, CancellationToken cancellation) => context.Response.SendFileAsync(path, cancellation);

    /// <summary>中止连接。</summary>
    public void Abort() => context.Abort();
}
