using Repositories;

namespace Core.Usage;

/// <summary>
/// 一次请求对上游的一次尝试的落库记录（表 usage_attempts）：挂在哪条用量下（随用量一起物理删除）、第几次（同一用量内唯一）、哪一家与它的名称快照、
/// 活动版本、开始时刻（UTC 刻度）、到响应头与总耗时、上游状态码与请求编号、错误类型与说明、连接失败分类、去向。转发时的边走边记由 <see cref="UpstreamAttempt"/> 承担。
/// </summary>
public sealed class UsageAttempt : BaseEntity
{
    /// <summary>所属用量记录的主键。</summary>
    public long UsageRequestId { get; set; }

    /// <summary>第几次尝试，从 1 开始。</summary>
    public int Attempt { get; set; }

    /// <summary>这次用的已保存上游主键；不是从已保存上游发出的为空。</summary>
    public long? UpstreamCandidateId { get; set; }

    /// <summary>这次用的活动上游版本。</summary>
    public long UpstreamVersion { get; set; }

    /// <summary>开始尝试的时刻（UTC 刻度）。</summary>
    public long StartedAt { get; set; }

    /// <summary>到响应头的毫秒数。</summary>
    public long? HeadersMs { get; set; }

    /// <summary>总耗时（毫秒）。</summary>
    public long DurationMs { get; set; }

    /// <summary>上游状态码。</summary>
    public int? UpstreamStatus { get; set; }

    /// <summary>上游请求编号。</summary>
    public string? UpstreamRequestId { get; set; }

    /// <summary>错误类型。</summary>
    public string? ErrorType { get; set; }

    /// <summary>脱敏截断后的错误说明。</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>连接失败分类。</summary>
    public string? ConnectFailure { get; set; }

    /// <summary>去向：client 或 next。</summary>
    public string Disposition { get; set; } = "client";

    /// <summary>记账那一刻那一家的名称快照。</summary>
    public string? UpstreamName { get; set; }

    /// <summary>把转发时记下的一次尝试做成落库记录。</summary>
    /// <param name="usageRequestId">所属用量记录主键。</param>
    /// <param name="attempt">转发时的尝试记录。</param>
    /// <param name="upstreamName">那一家此刻的名称。</param>
    public static UsageAttempt From(long usageRequestId, UpstreamAttempt attempt, string? upstreamName) => new()
    {
        UsageRequestId = usageRequestId, Attempt = attempt.Number, UpstreamCandidateId = attempt.UpstreamCandidateId, UpstreamVersion = attempt.Version,
        StartedAt = attempt.StartedAt.UtcTicks, HeadersMs = attempt.HeadersMs, DurationMs = attempt.DurationMs, UpstreamStatus = attempt.UpstreamStatus,
        UpstreamRequestId = attempt.UpstreamRequestId, ErrorType = attempt.ErrorType, ErrorMessage = attempt.ErrorMessage,
        ConnectFailure = attempt.ConnectFailure, Disposition = attempt.Disposition, UpstreamName = upstreamName,
    };
}
