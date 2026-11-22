namespace Infrastructure.ExternalSources.Siyu;

/// <summary>按 RFC 9112 §6.3 判出的消息体定界方式。</summary>
public enum Http1BodyFraming
{
    /// <summary>没有消息体。</summary>
    None,

    /// <summary>Content-Length 定长。</summary>
    Length,

    /// <summary>Transfer-Encoding: chunked，逐块定界（只出现在上游回应）。</summary>
    Chunked,

    /// <summary>读到连接关闭为止（只出现在上游回应；转发后关闭客户端内层连接）。</summary>
    UntilClose,
}
