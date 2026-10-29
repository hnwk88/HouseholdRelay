using System.Text.Json.Serialization;

namespace Core.Upstream;

/// <summary>请求持有的不可变活动版本；用普通类而非记录类型，避免自动输出凭据。</summary>
public sealed class UpstreamSnapshot(long version, UpstreamOptions options, string? credential, UpstreamReport? report = null)
{
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
