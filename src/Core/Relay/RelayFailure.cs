namespace Core.Relay;

/// <summary>中转请求的协议级失败：携带对客户端返回的状态码与可选重试秒数。</summary>
public sealed class RelayFailure(int status, string message, int retryAfter = 0) : Exception(message)
{
    /// <summary>要返回给客户端的 HTTP 状态码，例如 429 表示限流或排队失败、503 表示依赖不可用。</summary>
    public int Status { get; } = status;

    /// <summary>建议客户端等待后重试的秒数；0 表示不提供重试提示。</summary>
    public int RetryAfter { get; } = retryAfter;
}
