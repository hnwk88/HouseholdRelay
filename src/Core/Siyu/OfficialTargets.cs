namespace Core.Siyu;

/// <summary>
/// 思域 CONNECT 目标的唯一判据：只认两个官方主机的精确小写名字加 443 端口。
/// 不认 IP、别的端口、userinfo、尾点、子域、大小写变体或百分号编码；Name Constraints 是子树约束，这里另做精确比较。
/// </summary>
public static class OfficialTargets
{
    /// <summary>推理与账户接口主机。</summary>
    public const string ApiHost = "api.anthropic.com";

    /// <summary>官方连接器主机。</summary>
    public const string McpProxyHost = "mcp-proxy.anthropic.com";

    /// <summary>唯一允许的端口。</summary>
    public const int Port = 443;

    /// <summary>主机名（不含端口）。</summary>
    public static string Name(OfficialHost host) => host switch
    {
        OfficialHost.Api => ApiHost,
        OfficialHost.McpProxy => McpProxyHost,
        _ => throw new ArgumentOutOfRangeException(nameof(host), host, "不认识的官方主机。"),
    };

    /// <summary>CONNECT 的 authority 形式（<c>主机:443</c>），也是 CONNECT 请求里 Host 头必须等于的值。</summary>
    public static string Authority(OfficialHost host) => Name(host) + ":" + Port;

    /// <summary>CONNECT request-target 必须逐字等于某个官方目标的 <see cref="Authority"/>；否则返回 false。</summary>
    public static bool TryParseConnectTarget(string requestTarget, out OfficialHost host)
    {
        ArgumentNullException.ThrowIfNull(requestTarget);
        foreach (var candidate in new[] { OfficialHost.Api, OfficialHost.McpProxy })
            if (string.Equals(requestTarget, Authority(candidate), StringComparison.Ordinal)) { host = candidate; return true; }
        host = default;
        return false;
    }

    /// <summary>内层 TLS 的 SNI 或内层请求的 Host 是否就是这条隧道的官方主机（SNI 不带端口；Host 可带 :443 也可不带）。</summary>
    public static bool MatchesInnerName(OfficialHost host, string? name, bool allowPort)
    {
        if (name is null) return false;
        return string.Equals(name, Name(host), StringComparison.Ordinal) || allowPort && string.Equals(name, Authority(host), StringComparison.Ordinal);
    }
}
