namespace Infrastructure.ExternalSources.Siyu;

/// <summary>
/// 思域代理入口的时限与大小上限（版本 1 的固定值，测试可缩短）。
/// <see cref="HeadTimeout"/> 防慢速攻击；<see cref="IdleTimeout"/> 是内层连接两次请求之间最长空闲，超过就关闭，客户端会自己重连。
/// </summary>
public sealed record SiyuProxyLimits(long MaxBodyBytes, TimeSpan HeadTimeout, TimeSpan HandshakeTimeout, TimeSpan IdleTimeout)
{
    /// <summary>版本 1 默认值：头段 15 秒（与对外中转宿主相同）、TLS 握手 15 秒、空闲 120 秒；请求体上限沿用站点配置。</summary>
    public static SiyuProxyLimits Default(long maxBodyBytes) => new(maxBodyBytes, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(120));
}
