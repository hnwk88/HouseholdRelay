using System.Text.Json.Serialization;

namespace Core.Upstream;

/// <summary>请求持有的不可变活动版本；用普通类而非记录类型，避免自动输出凭据。</summary>
public sealed class UpstreamSnapshot(long version, UpstreamOptions options, string? credential, UpstreamReport? report = null, long? candidateId = null, string? name = null, bool failover = false, bool blockedByIdentity = false, string? accountUuid = null, string? blockedReason = null)
{
    /// <summary>本次读取被站点维护或私有身份配置校验拒绝；不是凭据丢失，连接池须保留既有暂停和冷却。</summary>
    [JsonIgnore] public bool BlockedByIdentity { get; } = blockedByIdentity;

    /// <summary>私有身份配置拒绝时的读句（固定说明，不含秘密），转发时原样告诉下游；站点维护或没有具体原因时为 null，用通用读句。</summary>
    [JsonIgnore] public string? BlockedReason { get; } = blockedReason;

    /// <summary>官方账户当前的 oauthAccount.accountUuid（取自账户目录私有配置），只在官方登录上游有值，用于把发往官方的 metadata.user_id 账户段对齐原生直连；非官方上游为 null，永不序列化。</summary>
    [JsonIgnore] public string? AccountUuid { get; } = accountUuid;

    /// <summary>当前生效的已保存上游主键；尚未从任何已保存上游激活（新实例或独立中转）时为 null。</summary>
    public long? UpstreamCandidateId { get; } = candidateId;

    /// <summary>当前生效上游的供应商名称，只做显示；改名立即反映在这里，不改变 <see cref="Version"/>。</summary>
    public string? Name { get; } = name;

    /// <summary>自动切换开关：在用的那一家失败时是否按顺序换到其他测试通过的上游；只在活动上游的快照上有意义，备用上游的快照恒为 false。</summary>
    public bool Failover { get; } = failover;

    /// <summary>
    /// 读这份快照时的活动上游版本：在用那一家和备用的各家记的都是它（备用上游没有自己的版本），连接池按它判断活动配置换没换、换了就重建连接，
    /// 用量与尝试记录也记它。哪一家靠 <see cref="UpstreamCandidateId"/> 区分，不靠这个数。
    /// </summary>
    public long ActiveVersion { get; } = version;

    /// <summary>该版本的非秘密连接配置。</summary>
    public UpstreamOptions Options { get; } = options;
    /// <summary>上游明文凭据；只供建连、脱敏和诊断使用，永不序列化。</summary>
    [JsonIgnore] public string? Credential { get; } = credential;
    /// <summary>本次快照是否取得可用凭据；维护拒绝的读取不可用于发送。</summary>
    public bool Configured => Credential is not null && !BlockedByIdentity;

    /// <summary>该版本最近一次检测结果；null 表示从未检测。</summary>
    public UpstreamReport? Report { get; } = report;
}
