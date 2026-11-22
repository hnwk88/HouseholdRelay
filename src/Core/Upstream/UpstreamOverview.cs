namespace Core.Upstream;

/// <summary>
/// 打开管理页面时看的活动上游：版本、非秘密配置、有没有可用凭据、最近一次检测报告、来源主键与名称、自动切换开关；不含凭据，读它不会等官方登录令牌刷新。
/// 转发要用凭据时读 <see cref="UpstreamSnapshot"/>。
/// </summary>
public sealed record UpstreamOverview(long Version, UpstreamOptions Options, bool Configured, UpstreamReport? Report, long? UpstreamCandidateId, string? Name, bool Failover = false)
{
    /// <summary>
    /// 在用上游此刻的运行状态读句，上游页与概览只读这一句：按优先级取第一个成立的——已导出搬走、未配置、凭据失效、冷却中（含剩余秒数）、已通过最小调用检测、调用尚未检测。
    /// </summary>
    /// <param name="moved">来源那一家已导出搬走。</param>
    /// <param name="paused">上游池因认证失败暂停了这个版本。</param>
    /// <param name="cooldownSeconds">上游池冷却的剩余秒数，没冷却为 0。</param>
    public string RuntimeText(bool moved, bool paused, int cooldownSeconds) => moved ? "已导出搬走，本机不再用它" : !Configured ? "未配置" : paused ? "凭据失效，需重新接入"
        : cooldownSeconds > 0 ? $"上游冷却中，约 {cooldownSeconds} 秒后重试" : Report?.CanActivate == true ? "已通过最小调用检测" : "已迁移凭据，调用尚未检测";
}
