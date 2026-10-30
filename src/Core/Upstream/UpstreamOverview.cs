namespace Core.Upstream;

/// <summary>
/// 打开管理页面时看的活动上游：版本、非秘密配置、有没有可用凭据、最近一次检测报告、来源主键与名称、自动切换开关；不含凭据，读它不会等官方登录令牌刷新。
/// 转发要用凭据时读 <see cref="UpstreamSnapshot"/>。
/// </summary>
public sealed record UpstreamOverview(long Version, UpstreamOptions Options, bool Configured, UpstreamReport? Report, string? CandidateId, string? Name, bool Failover = false);
