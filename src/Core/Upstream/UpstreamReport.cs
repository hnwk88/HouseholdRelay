using System.Text.Json.Serialization;

namespace Core.Upstream;

/// <summary>仅保存安全的检测状态；不保存上游原始响应或凭据。</summary>
public sealed record UpstreamReport(DateTimeOffset CheckedAt, CheckStatus Connection, CheckStatus Authentication, CheckStatus ModelDiscovery,
    CheckStatus Call, string[] Models, string? VerifiedModel, string Message)
{
    /// <summary>只有最小模型调用检测通过时才允许把该候选激活为活动版本；不参与序列化。</summary>
    [JsonIgnore] public bool CanActivate => Call == CheckStatus.Passed;
}
