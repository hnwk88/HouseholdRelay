namespace Application.Contracts.Siyu;

/// <summary>
/// 创建思域会话的请求（个人令牌另在 Authorization 头里）。<see cref="RequestId"/> 是 MyCoAI 生成的幂等键，按使用者与令牌隔离；
/// <see cref="ExpectedIdentityVersion"/> 新配置首次为 null，准备过的配置必须带已知代次；<see cref="BinarySha256"/> 只用来发现升级漂移，不当可信证明。
/// <see cref="OfficialAccountId"/> 版本 1 必带（客户端选账户），版本 2 不许带（后端选）。同一 <see cref="RequestId"/> 的重试必须逐项相同（记录相等即相同）。
/// </summary>
public sealed record SiyuCreateRequest(string RequestId, long? ExpectedIdentityVersion, int ClientProtocolVersion,
    string ClaudeVersion, string BinarySha256, string Entrypoint, string Transport, long? OfficialAccountId = null);
