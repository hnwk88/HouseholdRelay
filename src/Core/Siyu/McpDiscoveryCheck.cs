namespace Core.Siyu;

/// <summary>一次 MCP 连接器请求对照发现集合的结论。</summary>
public enum McpDiscoveryCheck
{
    /// <summary>连接器在当前账户、当前资料代次的有效集合里，可以放行。</summary>
    Permitted,

    /// <summary>没有有效集合（从没发现过、已过期、资料换代或已失效）；调用方可单飞补一次发现后再查，补不到就拒绝。</summary>
    NotDiscovered,

    /// <summary>有效集合里没有这个连接器：拒绝，不为它另补发现。</summary>
    Unknown,
}
