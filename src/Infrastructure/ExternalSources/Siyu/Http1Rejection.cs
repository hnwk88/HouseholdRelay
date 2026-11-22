namespace Infrastructure.ExternalSources.Siyu;

/// <summary>
/// 内层 HTTP/1.1 报文不在思域合同内：<see cref="Status"/> 是回给客户端的状态码，<see cref="Code"/> 是固定的英文原因码（只用于日志和测试，不念给用户）。
/// 抛出后这条内层连接不再解析后续字节，回本地错误后关闭。
/// </summary>
public sealed class Http1Rejection : Exception
{
    /// <summary>状态码与原因码都必填。</summary>
    public Http1Rejection(int status, string code) : base("内层请求不在思域合同内：" + code)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        Status = status; Code = code;
    }

    /// <summary>回给客户端的 HTTP 状态码。</summary>
    public int Status { get; }

    /// <summary>固定原因码，例如 <c>content-length</c>、<c>obs-fold</c>。</summary>
    public string Code { get; }
}
