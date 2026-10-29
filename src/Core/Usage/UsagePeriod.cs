namespace Core.Usage;

/// <summary>按所选时区的小时或日划分的一个时间段汇总。</summary>
public sealed record UsagePeriod(DateTimeOffset Start, UsageSummary Summary);
