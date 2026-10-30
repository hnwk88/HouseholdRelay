namespace Core.Usage;

/// <summary>用量明细里一次上游尝试的读出结果；<see cref="Name"/> 按 <see cref="CandidateId"/> 现查，只做显示，已删除或未保存的上游为 null。</summary>
public sealed record UpstreamAttemptRow(int Number, string? CandidateId, string? Name, long Version, DateTimeOffset StartedAt,
    long? HeadersMs, long DurationMs, int? UpstreamStatus, string? UpstreamRequestId, string? ErrorType, string? ErrorMessage,
    string? ConnectFailure, string Disposition);
