namespace Core.Usage;

/// <summary>用量明细中的一行：归属、时间、接口、结果、计数与金额，不含提示词或回复正文。</summary>
public sealed record UsageRow(string RequestId, string UserId, string DisplayName, string TokenId, long? UpstreamVersion,
    DateTimeOffset StartedAt, DateTimeOffset? EndedAt, string Kind, string? Model, string Outcome, bool Dispatched,
    int? StatusCode, int? UpstreamStatus, long? DurationMs, string Completeness, UsageTokens Tokens)
{
    /// <summary>这次请求的金额、币种、来源与完整性；默认是来源和完整性都为 unknown 的未知金额。</summary>
    public UsageMoney Money { get; init; } = new();
}
