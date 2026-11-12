using System.Text.Json.Serialization;

namespace Core.Upstream;

/// <summary>请求持有的不可变活动版本；用普通类而非记录类型，避免自动输出凭据。</summary>
public sealed class UpstreamSnapshot(long version, UpstreamOptions options, string? credential, UpstreamReport? report = null, string? candidateId = null, string? name = null, bool failover = false, bool blockedByIdentity = false)
{
    /// <summary>本次读取被站点维护或私有身份配置校验拒绝；不是凭据丢失，连接池须保留既有暂停和冷却。</summary>
    [JsonIgnore] public bool BlockedByIdentity { get; } = blockedByIdentity;

    /// <summary>当前生效的已保存上游主键；尚未从任何已保存上游激活（新实例或独立中转）时为 null。</summary>
    public string? CandidateId { get; } = candidateId;

    /// <summary>当前生效上游的供应商名称，只做显示；改名立即反映在这里，不改变 <see cref="Version"/>。</summary>
    public string? Name { get; } = name;

    /// <summary>自动切换开关：在用的那一家失败时是否按顺序换到其他测试通过的上游；只在活动上游的快照上有意义，备用上游的快照恒为 false。</summary>
    public bool Failover { get; } = failover;

    /// <summary>活动上游版本的编号，用于区分新旧活动版本、按版本回收连接；备用上游的快照记的是读取时的活动版本，哪一家靠 <see cref="CandidateId"/> 区分。</summary>
    public long Version { get; } = version;

    /// <summary>该版本的非秘密连接配置。</summary>
    public UpstreamOptions Options { get; } = options;
    /// <summary>上游明文凭据；只供建连、脱敏和诊断使用，永不序列化。</summary>
    [JsonIgnore] public string? Credential { get; } = credential;
    /// <summary>本次快照是否取得可用凭据；维护拒绝的读取不可用于发送。</summary>
    public bool Configured => Credential is not null && !BlockedByIdentity;

    /// <summary>该版本最近一次检测结果；null 表示从未检测。</summary>
    public UpstreamReport? Report { get; } = report;
}
