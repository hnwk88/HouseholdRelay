using Application.Interfaces.Relay;
using Core.Usage;

namespace Application.Interfaces.Upstream;

/// <summary>上游响应的安全转交：复制给客户端、丢弃前读出错误说明、脱敏文本。实现负责解压、限长、逐事件脱敏与计量。</summary>
public interface IUpstreamResponses
{
    /// <summary>把上游响应脱敏后转给客户端，并交给 <paramref name="usage"/> 计量。</summary>
    Task CopyAsync(HttpResponseMessage upstream, IRelayReply reply, string secret, string requestId, UsageAccumulator usage, CancellationToken cancellation);

    /// <summary>把上游响应脱敏后转给客户端，不计量。</summary>
    Task CopyAsync(HttpResponseMessage upstream, IRelayReply reply, string secret, string requestId, CancellationToken cancellation);

    /// <summary>要丢弃这家的错误响应时，只读出错误类型与说明交给 <paramref name="usage"/>，一个字节也不写给客户端。</summary>
    Task InspectAsync(HttpResponseMessage upstream, string secret, UsageAccumulator usage, CancellationToken cancellation);

    /// <summary>把文本里出现的 <paramref name="secret"/>（原文、URL 编码、Base64）替换成 [redacted]。</summary>
    string Redact(string value, string secret);

    /// <summary>把流读进内存，超过 <paramref name="limit"/> 字节时抛出。</summary>
    Task<byte[]> ReadBoundedAsync(Stream stream, int limit, CancellationToken cancellation);
}
