using System.Text.Json.Serialization;

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

    /// <summary>本页明细各行的上游尝试，按请求编号（用量记录主键）分组；只给管理页，个人用量接口序列化时不输出，上游诊断不对使用者开放。</summary>
    [JsonIgnore] public IReadOnlyDictionary<string, IReadOnlyList<UpstreamAttemptRow>> Attempts { get; init; } = new Dictionary<string, IReadOnlyList<UpstreamAttemptRow>>();

    /// <summary>明细一共几页（每页 <see cref="PageSize"/> 条）；没有记录时为 0。分页只在这里算，页面和脚本都读它。</summary>
    public long TotalPages => (Total + PageSize - 1) / PageSize;

    /// <summary>当前页前面还有没有页。</summary>
    public bool HasPrevious => Page > 1;

    /// <summary>当前页后面还有没有记录。</summary>
    public bool HasNext => (long)Page * PageSize < Total;

    /// <summary>用量取值的显示名表，供页面脚本把线上名读成中文；排在已有字段之后，原有字段不变。</summary>
    public UsageLabelTable Labels => UsageLabelTable.Instance;

    /// <summary>取某条明细的上游尝试，按次序排列；没有发往上游或不在本页时为空。</summary>
    public IReadOnlyList<UpstreamAttemptRow> AttemptsOf(string requestId) => Attempts.TryGetValue(requestId, out var list) ? list : [];
}
