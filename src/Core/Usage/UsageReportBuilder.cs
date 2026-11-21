using Core.Upstream;
using Core.Values;

namespace Core.Usage;

/// <summary>
/// 用量报告的纯计算：给定 40 天窗口内、已按筛选条件取出的用量记录，算出按接口类别的汇总、按小时或按日（用户时区）的分段、分币种与来源的金额合计、
/// 当页明细和当页各条的上游尝试。口径与库里聚合时逐项相同：计数全为空时合计为空（不是 0），金额按 decimal 累加、不同币种与来源从不相加，
/// 排序按序数比较（与 SQLite 默认的二进制排序一致）。所有数字出自同一次读取，互相对得上。
/// </summary>
public static class UsageReportBuilder
{
    /// <summary>明细的顺序：开始时刻新的在前，同一刻按请求编号倒序；只列归属使用者还在的记录。</summary>
    /// <param name="rows">窗口内的用量记录。</param>
    /// <param name="displayNames">使用者主键到显示名。</param>
    /// <param name="filter">筛选条件（取页码）。</param>
    public static IReadOnlyList<UsageRequest> Page(IEnumerable<UsageRequest> rows, IReadOnlyDictionary<long, string> displayNames, UsageFilter filter) =>
        rows.Where(row => displayNames.ContainsKey(row.UserId))
            .OrderByDescending(row => row.RequestStartedAt).ThenByDescending(row => row.RequestId, StringComparer.Ordinal)
            .Skip((filter.Page - 1) * UsageFilter.PageSize).Take(UsageFilter.PageSize).ToArray();

    /// <summary>
    /// 组装报告：<paramref name="rows"/> 是窗口内全部符合筛选的记录，<paramref name="page"/> 是 <see cref="Page"/> 圈出的当页，
    /// <paramref name="attempts"/> 是当页各条的上游尝试，<paramref name="candidates"/> 是这些尝试用过的已保存上游（含已停用的，用来补旧记录没有的名称快照、读出「已删除」）。
    /// </summary>
    /// <param name="rows">窗口内全部符合筛选的用量记录。</param>
    /// <param name="page">当页明细。</param>
    /// <param name="displayNames">使用者主键到显示名。</param>
    /// <param name="attempts">当页各条的上游尝试。</param>
    /// <param name="candidates">尝试用过的已保存上游。</param>
    /// <param name="filter">筛选条件。</param>
    /// <param name="cutoff">保留窗口的起点。</param>
    /// <param name="now">查询时刻。</param>
    /// <param name="persistenceFailures">本进程的用量保存失败次数。</param>
    public static UsageReport Build(IReadOnlyList<UsageRequest> rows, IReadOnlyList<UsageRequest> page, IReadOnlyDictionary<long, string> displayNames,
        IReadOnlyList<UsageAttempt> attempts, IReadOnlyDictionary<long, UpstreamCandidate> candidates, UsageFilter filter, DateTimeOffset cutoff, DateTimeOffset now, long persistenceFailures)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(filter.TimeZone);
        var totals = Money(rows, _ => 0L);
        var summary = rows.GroupBy(row => row.Kind).OrderBy(group => Wire<UsageKind>.Name(group.Key), StringComparer.Ordinal)
            .Select(group => Summary(group.Key, group) with { Money = totals.Where(total => total.Kind == group.Key).Select(total => total.Value).ToArray() }).ToArray();
        long Bucket(UsageRequest row) => UsageReportBuilder.Bucket(row.RequestStartedAt, filter.Granularity, zone);
        var timed = Money(rows, Bucket);
        var periods = rows.GroupBy(row => (Bucket: Bucket(row), row.Kind)).OrderByDescending(group => group.Key.Bucket).ThenBy(group => Wire<UsageKind>.Name(group.Key.Kind), StringComparer.Ordinal)
            .Select(group => new UsagePeriod(TimeZoneInfo.ConvertTime(new DateTimeOffset(group.Key.Bucket, TimeSpan.Zero), zone), Summary(group.Key.Kind, group) with
            { Money = timed.Where(total => total.Bucket == group.Key.Bucket && total.Kind == group.Key.Kind).Select(total => total.Value).ToArray() })).ToArray();
        var details = page.Select(row => new UsageRow(row.RequestId, row.UserId, displayNames[row.UserId], row.TokenId, row.UpstreamVersion,
            new DateTimeOffset(row.RequestStartedAt, TimeSpan.Zero), row.EndedAt is { } end ? new DateTimeOffset(end, TimeSpan.Zero) : null,
            row.Kind, row.Model, row.Outcome, row.Dispatched, row.StatusCode, row.UpstreamStatus, row.DurationMs, row.Completeness,
            new UsageTokens(row.InputTokens, row.OutputTokens, row.CacheReadTokens, row.CacheWriteTokens))
            { Money = new(row.Amount, row.Currency, row.MoneySource, row.MoneyCompleteness) }).ToArray();
        var requestIds = page.ToDictionary(row => row.Id, row => row.RequestId);
        var tried = attempts.Where(attempt => requestIds.ContainsKey(attempt.UsageRequestId))
            .OrderBy(attempt => requestIds[attempt.UsageRequestId], StringComparer.Ordinal).ThenBy(attempt => attempt.Attempt)
            .GroupBy(attempt => requestIds[attempt.UsageRequestId])
            .ToDictionary(group => group.Key, group => (IReadOnlyList<UpstreamAttemptRow>)group.Select(attempt => Attempt(attempt, candidates)).ToArray());
        return new(cutoff, now, filter.Page, UsageFilter.PageSize, summary.Sum(item => item.Requests), summary, details, persistenceFailures)
            { Periods = periods, Granularity = filter.Granularity, TimeZone = filter.TimeZone, Attempts = tried };
    }

    /// <summary>
    /// 把 UTC 刻度折算到用户时区的整点或当日零点，再换回 UTC 刻度作为分段键；零点因夏令时跳变不存在时顺延到第一个有效整点。
    /// </summary>
    /// <param name="ticks">UTC 刻度。</param>
    /// <param name="granularity">hour 或 day。</param>
    /// <param name="zone">用户时区。</param>
    public static long Bucket(long ticks, string granularity, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(new DateTimeOffset(ticks, TimeSpan.Zero), zone);
        if (granularity == "hour") return new DateTimeOffset(local.Year, local.Month, local.Day, local.Hour, 0, 0, local.Offset).UtcTicks;
        var midnight = DateTime.SpecifyKind(local.Date, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(midnight)) midnight = midnight.AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(midnight, zone).Ticks;
    }

    /// <summary>一组记录的分类汇总：请求数、各结果笔数、未发送、不完整、四项计数合计（全为空时为空）与金额来源未知的推理笔数。</summary>
    private static UsageSummary Summary(UsageKind kind, IEnumerable<UsageRequest> group)
    {
        var rows = group.ToArray();
        long Count(Func<UsageRequest, bool> predicate) => rows.LongCount(predicate);
        return new UsageSummary(kind, rows.LongLength, Count(row => row.Outcome == UsageOutcome.Succeeded), Count(row => row.Outcome == UsageOutcome.Failed), Count(row => row.Outcome == UsageOutcome.Cancelled),
            Count(row => row.Outcome == UsageOutcome.Interrupted), Count(row => row.Outcome == UsageOutcome.Started), Count(row => !row.Dispatched),
            Count(row => row.Completeness is UsageCompleteness.Partial or UsageCompleteness.NotProvided),
            new UsageTokens(Sum(rows, row => row.InputTokens), Sum(rows, row => row.OutputTokens), Sum(rows, row => row.CacheReadTokens), Sum(rows, row => row.CacheWriteTokens)))
            { UnknownAmounts = Count(row => row.Kind == UsageKind.Inference && row.MoneySource == MoneySource.Unknown) };
    }

    /// <summary>可空计数求和：全为空时为空（与 SQL 的 SUM 同口径），否则只加有值的。</summary>
    private static long? Sum(IEnumerable<UsageRequest> rows, Func<UsageRequest, long?> value)
    {
        long? total = null;
        foreach (var item in rows.Select(value)) if (item is { } number) total = (total ?? 0) + number;
        return total;
    }

    /// <summary>有金额的记录按（时间段、类别、币种、来源）分组合计，按币种、来源排序；<paramref name="bucket"/> 不分时间段时恒为 0。</summary>
    private static List<(long Bucket, UsageKind Kind, MoneyTotal Value)> Money(IEnumerable<UsageRequest> rows, Func<UsageRequest, long> bucket) =>
        rows.Where(row => row.Amount is not null)
            .GroupBy(row => (Bucket: bucket(row), row.Kind, Currency: row.Currency ?? "", Source: row.MoneySource))
            .OrderBy(group => group.Key.Currency, StringComparer.Ordinal).ThenBy(group => Wire<MoneySource>.Name(group.Key.Source), StringComparer.Ordinal)
            .Select(group => (group.Key.Bucket, group.Key.Kind, new MoneyTotal(group.Key.Currency, group.Key.Source, group.Sum(row => row.Amount!.Value),
                group.LongCount(), group.LongCount(row => row.MoneyCompleteness != MoneyCompleteness.Complete)))).ToList();

    /// <summary>一次尝试的读出结果：名称先取记账时的快照，旧记录没有快照时按候选主键现查（停用的上游照样查得到），并带出它此刻是否已停用。</summary>
    private static UpstreamAttemptRow Attempt(UsageAttempt attempt, IReadOnlyDictionary<long, UpstreamCandidate> candidates)
    {
        var candidate = attempt.UpstreamCandidateId is { } id && candidates.TryGetValue(id, out var found) ? found : null;
        return new(attempt.Attempt, attempt.UpstreamCandidateId, attempt.UpstreamName ?? candidate?.Name, attempt.UpstreamVersion,
            new DateTimeOffset(attempt.StartedAt, TimeSpan.Zero), attempt.HeadersMs, attempt.DurationMs, attempt.UpstreamStatus, attempt.UpstreamRequestId,
            attempt.ErrorType, attempt.ErrorMessage, attempt.ConnectFailure, attempt.Disposition) { Deleted = candidate?.IsDeleted ?? false };
    }
}
