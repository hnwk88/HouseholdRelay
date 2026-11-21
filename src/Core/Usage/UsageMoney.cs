namespace Core.Usage;

/// <summary>费用来源和完整性独立保存；上游报告金额优先，估算不能冒充实际扣费。</summary>
public sealed record UsageMoney(decimal? Amount = null, string? Currency = null, MoneySource Source = MoneySource.Unknown, MoneyCompleteness Completeness = MoneyCompleteness.Unknown);
