namespace Core.Usage;

/// <summary>一次用量查询的结果：窗口、分页、按类别和时间段的汇总及明细行。</summary>
public sealed record UsageReport(DateTimeOffset WindowStart, DateTimeOffset AsOf, int Page, int PageSize,
    long Total, IReadOnlyList<UsageSummary> Summary, IReadOnlyList<UsageRow> Rows, long PersistenceFailures)
{
    /// <summary>时间段汇总的粒度，取查询条件中的 <c>day</c> 或 <c>hour</c>。</summary>
    public string Granularity { get; init; } = "day";

    /// <summary>划分时间段所用的时区标识，与查询条件一致，默认 UTC。</summary>
    public string TimeZone { get; init; } = "UTC";

    /// <summary>按 <see cref="Granularity"/> 和 <see cref="TimeZone"/> 划分的各时间段、各接口类别汇总；没有数据时为空列表。</summary>
    public IReadOnlyList<UsagePeriod> Periods { get; init; } = [];
}
