namespace Core.Siyu;

/// <summary>
/// 思域协议（版本 1 与版本 2）的全部固定值，唯一定义处；不交给用户配置。
/// 租约、限额、保留期与 MCP 发现集合的时限还没在目标 Windows 上测过长时间断线和睡眠恢复，双机验收时实测，不合就改这里。
/// </summary>
public static class SiyuContract
{
    /// <summary>版本 1：客户端在建会话时带官方账户主键；拒绝不带打回。过渡期与版本 2 并存，行为与 2026-11-24 以前逐字相同。</summary>
    public const int LegacyProtocolVersion = 1;

    /// <summary>版本 2：建会话不带账户，后端按「在用」的上游选官方或标准模式；可自动处理的拒绝带打回（<see cref="SiyuRemedies"/>）。</summary>
    public const int ProtocolVersion = 2;

    /// <summary>中转同时认的协议版本；不在表里的拒建会话。</summary>
    public static IReadOnlyList<int> SupportedProtocolVersions { get; } = [LegacyProtocolVersion, ProtocolVersion];

    /// <summary>这个版本是不是由后端选上游、拒绝带打回（版本 2 起）。</summary>
    public static bool BackendChooses(int clientProtocolVersion) => clientProtocolVersion >= ProtocolVersion;

    /// <summary>唯一的请求策略：正文原样转发。</summary>
    public const string RequestStrategy = "nativePreserve";

    /// <summary>官方模式唯一的传输方式：原生 HTTPS 代理。</summary>
    public const string Transport = "nativeHttpsProxy";

    /// <summary>标准模式回应里的协议名：站点自己的标准 API 说 Anthropic Messages 协议（旧接法那一条）。</summary>
    public const string StandardProtocol = "anthropic-messages";

    /// <summary>标准模式回应里的认证方式：个人令牌（Authorization: Bearer 或 x-api-key），第三方上游的密钥只在中转机上。</summary>
    public const string StandardAuthentication = "personal-token";

    /// <summary>打回 wait 建议的等待秒数（维护中、名单暂时读不到、用量暂时记不下）。</summary>
    public const int RemedyWaitSeconds = 30;

    /// <summary>已验证的 Claude Code 版本（2.1.325 的真实旅程证据）；不在表里的版本拒建会话。</summary>
    public static IReadOnlyList<string> SupportedClaudeVersions { get; } = ["2.1.325"];

    /// <summary>已验证的入口：实测记录里只出现过 sdk-cli；交互式入口没实测，不放进来。</summary>
    public static IReadOnlyList<string> SupportedEntrypoints { get; } = ["sdk-cli"];

    /// <summary>
    /// 代理凭据（<c>HTTPS_PROXY=https://sessionId:ticket@站点:端口</c> 的用户名与密码）的字符集：
    /// sessionId 是 32 位小写十六进制；ticket 是 32 个随机字节去掉补位的 base64url（43 个字符）。
    /// 两样都只含 <c>[A-Za-z0-9_-]</c>，全是 RFC 3986 的非保留字符，MyCoAI 用 <c>Uri.EscapeDataString</c> 处理后原样不变。
    /// 代理入口只做 Basic 的 base64 解码、不做百分号解码，同一个值只有一种写法。
    /// </summary>
    public const string ProxyCredentialAlphabet = "A-Za-z0-9_-";

    /// <summary>后台清扫（租约到期、终态与幂等结果过期）的间隔。</summary>
    public static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(5);

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
