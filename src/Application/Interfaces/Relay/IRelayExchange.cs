using Core.Relay;
using Microsoft.Extensions.Primitives;

namespace Application.Interfaces.Relay;

/// <summary>
/// 一次中转请求交给转发服务的全部东西：与协议框架无关的请求字段、请求体流、回话出口和按 Anthropic 格式写错误的出口。
/// 宿主把自己的请求上下文包成它；转发服务只经它读请求、写回应。
/// </summary>
public interface IRelayExchange
{
    /// <summary>请求路径，例如 /v1/messages；没有时为「/」。</summary>
    string Path { get; }

    /// <summary>请求方法，大写，例如 POST。</summary>
    string Method { get; }

    /// <summary>原样的查询串（含开头的「?」）；没有时为空串。</summary>
    string QueryString { get; }

    /// <summary>请求的 Content-Type；没有时为 null。</summary>
    string? ContentType { get; }

    /// <summary>请求声明的正文长度；没有声明时为 null。</summary>
    long? ContentLength { get; }

    /// <summary>全部请求头，原样交给上游请求的组装。</summary>
    IDictionary<string, StringValues> Headers { get; }

    /// <summary>查询参数（键不区分大小写）。</summary>
    IReadOnlyDictionary<string, StringValues> Query { get; }

    /// <summary>读一段请求体，读完返回 0。宿主读请求时发现格式错误或超限，抛出带原状态码与「Invalid or oversized request.」的 <see cref="RelayFailure"/>。</summary>
    ValueTask<int> Read(Memory<byte> buffer, CancellationToken cancellation);

    /// <summary>客户端断开时取消。</summary>
    CancellationToken Aborted { get; }

    /// <summary>回话出口。</summary>
    IRelayReply Reply { get; }

    /// <summary>取一个请求头的值；没有时为空值。</summary>
    StringValues Header(string name);

    /// <summary>按 Anthropic 错误格式写出中转失败；响应已开始或客户端已断开时直接中止连接。</summary>
    Task Fail(RelayFailure error);
}
