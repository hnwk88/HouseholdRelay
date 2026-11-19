namespace Core.Usage;

/// <summary>
/// 用量明细里一次上游尝试的读出结果；<see cref="Name"/> 只做显示：先取记账那一刻存下的名称快照，没有快照的旧记录按 <see cref="CandidateId"/> 现查，
/// 不是从已保存上游发出的为 null；名称为空串表示那一家没有名字。
/// </summary>
public sealed record UpstreamAttemptRow(int Number, string? CandidateId, string? Name, long Version, DateTimeOffset StartedAt,
    long? HeadersMs, long DurationMs, int? UpstreamStatus, string? UpstreamRequestId, string? ErrorType, string? ErrorMessage,
    string? ConnectFailure, string Disposition)
{
    /// <summary>那一家此刻已被删除（停用）；读句在名字后面加「（已删除）」。</summary>
    public bool Deleted { get; init; }
}
