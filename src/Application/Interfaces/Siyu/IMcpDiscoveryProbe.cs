namespace Application.Interfaces.Siyu;

/// <summary>
/// 服务端单飞补一次连接器发现：把客户端曾经真实发出的发现请求头原样重发给官方（只把 Authorization 换成 <paramref name="accessToken"/>），
/// 读回 200 的清单并取出连接器编号。不是 200、读不懂或超时返回 null；不重试。请求头的每个值都来自真实客户端，服务端不自己拼。
/// </summary>
public interface IMcpDiscoveryProbe
{
    /// <summary>重发一次发现；成功返回编号，失败返回 null。</summary>
    Task<IReadOnlyList<string>?> DiscoverAsync(byte[] requestTemplate, string accessToken, CancellationToken cancellation);
}
