using System.Text.Json.Serialization;

namespace Core.Upstream;

/// <summary>请求持有的不可变活动版本；用普通类而非记录类型，避免自动输出凭据。</summary>
public sealed class UpstreamSnapshot(long version, UpstreamOptions options, string? credential, UpstreamReport? report = null, string? candidateId = null, string? name = null)
{
    /// <summary>当前生效的已保存上游主键；尚未从任何已保存上游激活（新实例或独立中转）时为 null。</summary>
    public string? CandidateId { get; } = candidateId;

    /// <summary>当前生效上游的供应商名称，只做显示；改名立即反映在这里，不改变 <see cref="Version"/>。</summary>
    public string? Name { get; } = name;

    /// <summary>该上游版本的编号，用于区分新旧活动版本、按版本回收连接。</summary>
    public long Version { get; } = version;

    /// <summary>该版本的非秘密连接配置。</summary>
    public UpstreamOptions Options { get; } = options;
    /// <summary>上游明文凭据；只供建连、脱敏和诊断使用，永不序列化。</summary>
    [JsonIgnore] public string? Credential { get; } = credential;
    /// <summary>是否已有可用凭据；为 false 时中转请求会以「未配置」失败。</summary>
    public bool Configured => Credential is not null;

    /// <summary>该版本最近一次检测结果；null 表示从未检测。</summary>
    public UpstreamReport? Report { get; } = report;
}
