namespace Core.Siyu;

/// <summary>
/// 思域会话创建时固定下来的绑定，运行中不替换：服务实例、会话、使用者与个人令牌（主键与代次）、官方账户（上游主键；标准模式为 null）、站点身份代次、资料代次（标准模式为空串）、协议版本、客户端版本与入口。
/// 关联全部走主键；Claude 的 session_id 与 MCP 的 Mcp-Session-Id 只是协议数据，不在这里。
/// </summary>
public sealed record SiyuBinding(string ServerInstanceId, string SessionId, long UserId, long TokenId, int TokenVersion, long? OfficialAccountId,
    long SiteIdentityVersion, string ProfileVersion, int ClientProtocolVersion, string ClaudeVersion, string Entrypoint)
{
    /// <summary>模式：绑了官方账户就是官方模式，否则是标准模式（只有版本 2 会有）。</summary>
    public SiyuMode Mode => OfficialAccountId is null ? SiyuMode.Standard : SiyuMode.Official;
}
