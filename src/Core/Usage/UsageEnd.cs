namespace Core.Usage;

/// <summary>一次中转请求结束时的结果、计数与上游报告金额，用于补全开始时写下的用量记录。</summary>
public sealed record UsageEnd(string Outcome, bool Dispatched, int Status, int? UpstreamStatus,
    long DurationMs, string Completeness, UsageTokens Tokens, UsageMoney? ReportedMoney = null);
