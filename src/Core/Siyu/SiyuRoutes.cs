using Core.Relay;

namespace Core.Siyu;

/// <summary>
/// 思域内层请求按「主机 + 方法 + origin-form 路径」放行的唯一登记处。推理三条沿用 <see cref="RelayRoutes"/> 的常量。
/// 只认精确路径：百分号编码、点段、双斜杠、片段、绝对形式一律不匹配；查询串只在登记了的路由上、按字符集和长度放行。
/// </summary>
public static class SiyuRoutes
{
    /// <summary>远程设置。</summary>
    public const string Settings = "/api/claude_code/settings";

    /// <summary>限额策略。</summary>
    public const string PolicyLimits = "/api/claude_code/policy_limits";

    /// <summary>连接器发现。</summary>
    public const string McpServers = "/v1/mcp_servers";

    /// <summary>连接器传输路径前缀，后接连接器编号。</summary>
    public const string McpConnectorPrefix = "/v1/mcp/";

    /// <summary>连接器编号最长字符数。</summary>
    public const int MaxConnectorIdLength = 128;

    /// <summary>查询串最长字符数（不含问号）。</summary>
    public const int MaxQueryLength = 256;

    /// <summary>
    /// 按主机、方法和 request-target 找路由；找不到返回 false。推理与估算只认空查询或 <c>beta=true</c>（实测客户端就发这一种）；
    /// 模型目录、设置、限额、发现允许有界查询串；连接器路径不带查询。连接器支持 POST、GET、DELETE（Streamable HTTP）。
    /// </summary>
    public static bool TryMatch(OfficialHost host, string method, string requestTarget, out SiyuRoute route)
    {
        ArgumentNullException.ThrowIfNull(method); ArgumentNullException.ThrowIfNull(requestTarget);
        route = default;
        if (!requestTarget.StartsWith('/') || requestTarget.Contains('%') || requestTarget.Contains('#') || requestTarget.Contains("//", StringComparison.Ordinal)
            || requestTarget.Contains("/./", StringComparison.Ordinal) || requestTarget.Contains("/../", StringComparison.Ordinal)) return false;
        var question = requestTarget.IndexOf('?');
        var path = question < 0 ? requestTarget : requestTarget[..question];
        var query = question < 0 ? null : requestTarget[(question + 1)..];
        if (path.EndsWith("/.", StringComparison.Ordinal) || path.EndsWith("/..", StringComparison.Ordinal)) return false;
        if (host == OfficialHost.Api)
        {
            SiyuPurpose? purpose = (method, path) switch
            {
                ("POST", RelayRoutes.Messages) when query is null or "beta=true" => SiyuPurpose.Messages,
                ("POST", RelayRoutes.CountTokens) when query is null or "beta=true" => SiyuPurpose.CountTokens,
                ("GET", RelayRoutes.Models) when SafeQuery(query) => SiyuPurpose.Models,
                ("GET", Settings) when SafeQuery(query) => SiyuPurpose.Settings,
                ("GET", PolicyLimits) when SafeQuery(query) => SiyuPurpose.PolicyLimits,
                ("GET", McpServers) when SafeQuery(query) => SiyuPurpose.McpServers,
                _ => null,
            };
            if (purpose is null) return false;
            route = new SiyuRoute(purpose.Value, null);
            return true;
        }
        if (host == OfficialHost.McpProxy)
        {
            if (query is not null || method is not ("POST" or "GET" or "DELETE") || !path.StartsWith(McpConnectorPrefix, StringComparison.Ordinal)) return false;
            var id = path[McpConnectorPrefix.Length..];
            if (id.Length is 0 or > MaxConnectorIdLength || !id.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-')) return false;
            route = new SiyuRoute(SiyuPurpose.McpConnector, id);
            return true;
        }
        return false;
    }

    /// <summary>查询串可以没有；有就只许字母数字与 <c>_ - . = &amp; ,</c>，非空且不超过 <see cref="MaxQueryLength"/>。</summary>
    private static bool SafeQuery(string? query) =>
        query is null || query.Length is > 0 and <= MaxQueryLength && query.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-' or '.' or '=' or '&' or ',');
}
