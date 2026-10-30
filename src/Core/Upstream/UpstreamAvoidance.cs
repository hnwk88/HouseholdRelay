namespace Core.Upstream;

/// <summary>
/// 一家上游此刻为什么排不到前面：<c>avoid</c>（换掉后暂避 5 分钟）、<c>cooldown</c>（429 的 Retry-After 冷却）或 <c>paused</c>（401、403 认证暂停，没有到期时刻）；
/// 带最后一次失败的状态码、错误类型和连接失败分类，只做读出，不含秘密。哪一家按已保存上游主键关联，名称由页面另查。
/// </summary>
public sealed record UpstreamAvoidance(string? CandidateId, string Kind, DateTimeOffset? Until, int? Status, string? ErrorType, string? ConnectFailure);
