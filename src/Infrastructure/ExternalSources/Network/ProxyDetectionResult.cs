namespace Infrastructure.ExternalSources.Network;

/// <summary>
/// <see cref="WindowsSystemProxyDetector.Detect"/> 的探测结果（照 MyCoAI 同名类型移植）：<paramref name="HttpProxy"/>、<paramref name="HttpsProxy"/>
/// 为对应协议应走的代理，null 表示系统要求直连；<paramref name="NoProxy"/> 是逗号分隔的绕过列表，始终含本机回环地址。
/// </summary>
public sealed record ProxyDetectionResult(string? HttpProxy, string? HttpsProxy, string? NoProxy)
{
    /// <summary>是否检测到了任何一种协议的代理；为假时说明系统对这个目标不提供代理。</summary>
    public bool HasProxy => !string.IsNullOrWhiteSpace(HttpProxy) || !string.IsNullOrWhiteSpace(HttpsProxy);
}
