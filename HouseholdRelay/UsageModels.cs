namespace HouseholdRelay;

/// <summary>上游实际报告的可空计数；估算只使用输入列，缓存用量独立展示。</summary>
public sealed record UsageTokens(long? Input = null, long? Output = null, long? CacheRead = null, long? CacheWrite = null);

public sealed record UsageEnd(string Outcome, bool Dispatched, int Status, int? UpstreamStatus,
    long DurationMs, string Completeness, UsageTokens Tokens, UsageMoney? ReportedMoney = null);

public sealed record UsageRow(string RequestId, string UserId, string DisplayName, string TokenId, long? UpstreamVersion,
    DateTimeOffset StartedAt, DateTimeOffset? EndedAt, string Kind, string? Model, string Outcome, bool Dispatched,
    int? StatusCode, int? UpstreamStatus, long? DurationMs, string Completeness, UsageTokens Tokens)
{
    public UsageMoney Money { get; init; } = new();
}

public sealed record UsageSummary(string Kind, long Requests, long Succeeded, long Failed, long Cancelled,
    long Interrupted, long Pending, long NotSent, long Incomplete, UsageTokens Tokens)
{
    public long UnknownAmounts { get; init; }
    public IReadOnlyList<MoneyTotal> Money { get; init; } = [];
}

public sealed record UsagePeriod(DateTimeOffset Start, UsageSummary Summary);

public sealed record UsageReport(DateTimeOffset WindowStart, DateTimeOffset AsOf, int Page, int PageSize,
    long Total, IReadOnlyList<UsageSummary> Summary, IReadOnlyList<UsageRow> Rows, long PersistenceFailures)
{
    public string Granularity { get; init; } = "day";
    public string TimeZone { get; init; } = "UTC";
    public IReadOnlyList<UsagePeriod> Periods { get; init; } = [];
}

/// <summary>两种入口共用筛选合同；个人身份由服务器覆盖，不能由筛选参数提供。</summary>
public sealed record UsageFilter(string? UserId = null, DateTimeOffset? From = null, DateTimeOffset? Until = null,
    string? Kind = null, string? Model = null, string? Outcome = null, int Page = 1, string Granularity = "day", string TimeZone = "UTC")
{
    public const int PageSize = 25;
    public void Validate()
    {
        if (Page is < 1 or > 1000000 || From > Until || Model?.Length > 160 || UserId?.Length > 64
            || Granularity is not ("day" or "hour") || TimeZone.Length is < 1 or > 128
            || Kind is not (null or "" or "inference" or "estimate" or "models")
            || Outcome is not (null or "" or "started" or "succeeded" or "failed" or "cancelled" or "interrupted"))
            throw new InvalidOperationException("查询条件无效，请检查时间、接口、结果和页码。");
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(TimeZone); }
        catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException)
        { throw new InvalidOperationException("查询时区无效。"); }
    }
}

public static class UsageLabels
{
    public static string Kind(string value) => value switch { "inference" => "推理", "estimate" => "估算", "models" => "模型查询", _ => value };
    public static string Outcome(string value) => value switch
    { "started" => "处理中 / 待写入", "succeeded" => "成功", "failed" => "失败", "cancelled" => "已取消", "interrupted" => "中断", _ => value };
    public static string Completeness(string value) => value switch
    { "complete" => "完整", "partial" => "部分 / 不完整", "not_provided" => "上游未提供", "not_applicable" => "不适用", _ => value };
    public static string Count(long? value) => value?.ToString("N0") ?? "未知";
    public static string MoneySource(string value) => value switch { "reported" => "上游报告", "estimated" => "估算", "not_applicable" => "不适用", _ => "未知" };
    public static string Money(UsageMoney value) => value.Source == "not_applicable" ? "不适用" : value.Amount is null ? "未知"
        : value.Amount.Value.ToString("0.#########") + " " + value.Currency + " · " + MoneySource(value.Source) + (value.Completeness == "partial" ? "（部分）" : "");
}
