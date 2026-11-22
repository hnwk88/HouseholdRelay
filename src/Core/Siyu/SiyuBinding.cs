namespace Core.Siyu;

/// <summary>
/// 思域会话创建时固定下来的绑定，运行中不替换：服务实例、会话、使用者与个人令牌（主键与代次）、官方账户（上游主键）、站点身份代次、资料代次、协议版本、客户端版本与入口。
/// 关联全部走主键；Claude 的 session_id 与 MCP 的 Mcp-Session-Id 只是协议数据，不在这里。
/// </summary>
public sealed record SiyuBinding(string ServerInstanceId, string SessionId, long UserId, long TokenId, int TokenVersion, long OfficialAccountId,
    long SiteIdentityVersion, string ProfileVersion, int ClientProtocolVersion, string ClaudeVersion, string Entrypoint);
