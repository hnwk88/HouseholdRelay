namespace Core.Usage;

/// <summary>一次中转请求结束时的结果、计数与上游报告金额，用于补全开始时写下的用量记录。</summary>
public sealed record UsageEnd(string Outcome, bool Dispatched, int Status, int? UpstreamStatus,
    long DurationMs, string Completeness, UsageTokens Tokens, UsageMoney? ReportedMoney = null)
{
    /// <summary>这次请求对上游的各次尝试，按次序排列；没有发往上游时为空。与结束结果在同一个事务里写入。</summary>
    public IReadOnlyList<UpstreamAttempt> Attempts { get; init; } = [];
}
