namespace Application.Contracts.Upstream;

/// <summary>导入文件里一家上游的结果：文件里的名称；导入成功时是新上游的主键，失败时是读给管理员的原因（不含任何秘密）。</summary>
public sealed record UpstreamImportOutcome(string Name, string? CandidateId, string? Error);
