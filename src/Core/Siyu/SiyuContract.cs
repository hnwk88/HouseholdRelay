namespace Core.Siyu;

/// <summary>
/// 思域协议版本 1 的全部固定值，唯一定义处；不交给用户配置。
/// 租约、限额、保留期与 MCP 发现集合的时限还没在目标 Windows 上测过长时间断线和睡眠恢复，双机验收时实测，不合就改这里。
/// </summary>
public static class SiyuContract
{
    /// <summary>客户端必须声明的协议版本。</summary>
    public const int ProtocolVersion = 1;

    /// <summary>版本 1 唯一的请求策略：正文原样转发。</summary>
    public const string RequestStrategy = "nativePreserve";

    /// <summary>版本 1 唯一的传输方式：原生 HTTPS 代理。</summary>
    public const string Transport = "nativeHttpsProxy";

    /// <summary>一次续租给的存活时长；MyCoAI 失联后票据最多再有效这么久。</summary>
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(120);

    /// <summary>建议续租时刻（距上次确认）；是租约的三分之一，中间能错过两次续租。</summary>
    public static readonly TimeSpan LeaseRenewAfter = TimeSpan.FromSeconds(40);

    /// <summary>每个使用者同时存活的会话上限。</summary>
    public const int MaxSessionsPerUser = 8;

    /// <summary>每个会话同时打开的代理隧道上限；实测一整趟原生旅程共开 9 条。</summary>
    public const int MaxTunnelsPerSession = 16;

    /// <summary>创建请求按 requestId 重放原结果的保留期。</summary>
    public static readonly TimeSpan CreateReplayRetention = TimeSpan.FromMinutes(10);

    /// <summary>会话关闭后终态留给 GET、DELETE 确认的保留期；过了只回 404。</summary>
    public static readonly TimeSpan ClosedRetention = TimeSpan.FromMinutes(10);

    /// <summary>一次成功的 MCP 发现集合从发现开始算起的有效期。</summary>
    public static readonly TimeSpan McpDiscoveryLifetime = TimeSpan.FromMinutes(10);

    /// <summary>服务端单飞补一次 MCP 发现的限时。</summary>
    public static readonly TimeSpan McpDiscoveryTimeout = TimeSpan.FromSeconds(15);
}
