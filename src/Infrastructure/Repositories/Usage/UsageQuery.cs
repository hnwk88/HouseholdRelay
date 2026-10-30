using System.Globalization;
using Microsoft.Data.Sqlite;
using Core.Usage;
using Infrastructure.Persistence;
using static Infrastructure.Persistence.RelayDatabase;

namespace Infrastructure.Repositories.Usage;

/// <summary>明细、金额及时间统计共用只读快照和 UTC 窗口；金额按十进制汇总并隔离币种和来源。</summary>
internal static class UsageQuery
{
    private const string Metrics = """
        r.kind,COUNT(*),SUM(r.outcome='succeeded'),SUM(r.outcome='failed'),SUM(r.outcome='cancelled'),
        SUM(r.outcome='interrupted'),SUM(r.outcome='started'),SUM(r.dispatched=0),
        SUM(r.completeness IN ('partial','not_provided')),
        SUM(r.input_tokens),SUM(r.output_tokens),SUM(r.cache_read_tokens),SUM(r.cache_write_tokens),
        SUM(r.kind='inference' AND r.money_source='unknown')
        """;

    /// <summary>按筛选条件生成用量报告：起点不早于 40 天保留窗口，条件一律参数化（列名来自固定白名单）；在忙等 2 秒的连接上开启延迟事务，让分类汇总、金额合计、按小时或按日（用户时区）分段统计和分页明细读自同一快照，结果互相对得上。</summary>
    internal static UsageReport Read(RelayDatabase database, UsageFilter filter, DateTimeOffset now, long persistenceFailures)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(filter.TimeZone);
        var cutoff = now.AddDays(-40);
        var from = filter.From is { } since && since > cutoff ? since : cutoff;
        var where = " r.request_started_at >= $from AND r.request_started_at <= $now";
        var parameters = new List<(string Name, object? Value)> { ("$from", from.UtcTicks), ("$now", now.UtcTicks) };
        if (filter.Until is { } until) { where += " AND r.request_started_at < $until"; parameters.Add(("$until", until.UtcTicks)); }
        foreach (var (column, value) in new[] { ("user_id", filter.UserId), ("kind", filter.Kind), ("model", filter.Model), ("outcome", filter.Outcome) })
        {
            if (string.IsNullOrEmpty(value)) continue;
            where += " AND r." + column + "=$" + column; parameters.Add(("$" + column, value));
        }
        using var connection = database.Open(2);
        connection.CreateAggregate<string?, decimal?, string?>("decimal_sum", null,
            (sum, value) => value is null ? sum : (sum ?? 0m) + decimal.Parse(value, CultureInfo.InvariantCulture), UsageMoneyRules.DecimalText);
        connection.CreateFunction<long, long>("usage_bucket", ticks => Bucket(ticks, filter.Granularity, zone));
        using var transaction = connection.BeginTransaction(deferred: true);
        var args = parameters.ToArray(); var summary = new List<UsageSummary>();
        using (var command = Command(connection, transaction, "SELECT " + Metrics + " FROM usage_requests r WHERE" + where + " GROUP BY r.kind", args))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) summary.Add(Summary(reader, 0));
        var totals = Money(connection, transaction, where, args, false);
        summary = summary.Select(item => item with { Money = totals.Where(total => total.Kind == item.Kind).Select(total => total.Value).ToArray() }).ToList();
        var periods = new List<UsagePeriod>();
        using (var command = Command(connection, transaction, "SELECT usage_bucket(r.request_started_at) bucket," + Metrics
            + " FROM usage_requests r WHERE" + where + " GROUP BY bucket,r.kind ORDER BY bucket DESC,r.kind", args))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) periods.Add(new(TimeZoneInfo.ConvertTime(new DateTimeOffset(reader.GetInt64(0), TimeSpan.Zero), zone), Summary(reader, 1)));
        var timedMoney = Money(connection, transaction, where, args, true);
        periods = periods.Select(period => period with { Summary = period.Summary with
            { Money = timedMoney.Where(total => total.Bucket == period.Start.UtcTicks && total.Kind == period.Summary.Kind).Select(total => total.Value).ToArray() } }).ToList();
        parameters.Add(("$limit", UsageFilter.PageSize)); parameters.Add(("$offset", (filter.Page - 1) * UsageFilter.PageSize));
        var rows = new List<UsageRow>();
        using (var command = Command(connection, transaction, """
            SELECT r.request_id,r.user_id,u.display_name,r.token_id,r.upstream_version,r.request_started_at,r.ended_at,
                r.kind,r.model,r.outcome,r.dispatched,r.status_code,r.upstream_status,r.duration_ms,r.completeness,
                r.input_tokens,r.output_tokens,r.cache_read_tokens,r.cache_write_tokens,r.amount,r.currency,r.money_source,r.money_completeness
            FROM usage_requests r JOIN users u ON u.id=r.user_id WHERE
            """ + where + " ORDER BY r.request_started_at DESC,r.request_id DESC LIMIT $limit OFFSET $offset", parameters.ToArray()))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                Number(reader, 4), new DateTimeOffset(reader.GetInt64(5), TimeSpan.Zero),
                Number(reader, 6) is { } end ? new DateTimeOffset(end, TimeSpan.Zero) : null,
                reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetString(9), reader.GetBoolean(10),
                (int?)Number(reader, 11), (int?)Number(reader, 12), Number(reader, 13), reader.GetString(14), Tokens(reader, 15))
                { Money = new(reader.IsDBNull(19) ? null : decimal.Parse(reader.GetString(19), CultureInfo.InvariantCulture),
                    reader.IsDBNull(20) ? null : reader.GetString(20), reader.GetString(21), reader.GetString(22)) });
        var attempts = Attempts(connection, transaction, where, parameters.ToArray());
        transaction.Commit();
        return new(cutoff, now, filter.Page, UsageFilter.PageSize, summary.Sum(item => item.Requests), summary, rows, persistenceFailures)
            { Periods = periods, Granularity = filter.Granularity, TimeZone = filter.TimeZone, Attempts = attempts };
    }

    /// <summary>在同一事务内汇总有金额的请求：按类型、币种和金额来源分组，用自定义的十进制求和避免浮点误差，并统计笔数与金额不完整的笔数；<paramref name="timed"/> 为真时再按时间段分组，否则时间段固定为 0。</summary>
    private static List<(long Bucket, string Kind, MoneyTotal Value)> Money(SqliteConnection connection, SqliteTransaction transaction,
        string where, (string Name, object? Value)[] args, bool timed)
    {
        var bucket = timed ? "usage_bucket(r.request_started_at)" : "0";
        using var command = Command(connection, transaction, "SELECT " + bucket + " bucket,r.kind,r.currency,r.money_source,decimal_sum(r.amount),COUNT(*),SUM(r.money_completeness!='complete')"
            + " FROM usage_requests r WHERE" + where + " AND r.amount IS NOT NULL GROUP BY bucket,r.kind,r.currency,r.money_source ORDER BY r.currency,r.money_source", args);
        using var reader = command.ExecuteReader(); var result = new List<(long, string, MoneyTotal)>();
        while (reader.Read()) result.Add((reader.GetInt64(0), reader.GetString(1), new(reader.GetString(2), reader.GetString(3),
            decimal.Parse(reader.GetString(4), CultureInfo.InvariantCulture), reader.GetInt64(5), reader.GetInt64(6))));
        return result;
    }
    /// <summary>在同一事务内读出本页明细各行的上游尝试，按请求编号分组、按次序排列；名称按候选主键现查，候选已删除时为 null。本页用与明细相同的条件、排序和分页圈定。</summary>
    private static Dictionary<string, IReadOnlyList<UpstreamAttemptRow>> Attempts(SqliteConnection connection, SqliteTransaction transaction, string where, (string Name, object? Value)[] args)
    {
        using var command = Command(connection, transaction, """
            SELECT a.request_id,a.attempt,a.candidate_id,c.name,a.upstream_version,a.started_at,a.headers_ms,a.duration_ms,a.upstream_status,
                a.upstream_request_id,a.error_type,a.error_message,a.connect_failure,a.disposition
            FROM usage_attempts a LEFT JOIN upstream_candidates c ON c.id=a.candidate_id
            WHERE a.request_id IN (SELECT r.request_id FROM usage_requests r WHERE
            """ + where + " ORDER BY r.request_started_at DESC,r.request_id DESC LIMIT $limit OFFSET $offset) ORDER BY a.request_id,a.attempt", args);
        using var reader = command.ExecuteReader(); var result = new Dictionary<string, List<UpstreamAttemptRow>>();
        string? Text(int column) => reader.IsDBNull(column) ? null : reader.GetString(column);
        while (reader.Read())
        {
            if (!result.TryGetValue(reader.GetString(0), out var list)) result[reader.GetString(0)] = list = [];
            list.Add(new(reader.GetInt32(1), Text(2), Text(3), reader.GetInt64(4), new DateTimeOffset(reader.GetInt64(5), TimeSpan.Zero), Number(reader, 6),
                reader.GetInt64(7), (int?)Number(reader, 8), Text(9), Text(10), Text(11), Text(12), reader.GetString(13)));
        }
        return result.ToDictionary(item => item.Key, item => (IReadOnlyList<UpstreamAttemptRow>)item.Value);
    }

    /// <summary>从 <see cref="Metrics"/> 列组读出一条分类汇总，<paramref name="offset"/> 是这组列在结果行中的起始列号（分段查询前面多一列时间段）。</summary>
    private static UsageSummary Summary(SqliteDataReader reader, int offset) => new(reader.GetString(offset), reader.GetInt64(offset + 1), reader.GetInt64(offset + 2), reader.GetInt64(offset + 3),
        reader.GetInt64(offset + 4), reader.GetInt64(offset + 5), reader.GetInt64(offset + 6), reader.GetInt64(offset + 7), reader.GetInt64(offset + 8), Tokens(reader, offset + 9))
        { UnknownAmounts = reader.GetInt64(offset + 13) };
    /// <summary>读取可空整数列，数据库 NULL 返回 null，用来区分「未提供」与 0。</summary>
    private static long? Number(SqliteDataReader reader, int column) => reader.IsDBNull(column) ? null : reader.GetInt64(column);
    /// <summary>从起始列开始依次读出输入、输出、缓存读取、缓存写入四个令牌数，各自可为 null。</summary>
    private static UsageTokens Tokens(SqliteDataReader reader, int offset) => new(Number(reader, offset), Number(reader, offset + 1), Number(reader, offset + 2), Number(reader, offset + 3));
    /// <summary>把 UTC 时刻折算到用户时区的整点或当日零点，再换回 UTC 刻度作为分段键；零点因夏令时跳变不存在时顺延到第一个有效整点。供 SQL 函数 usage_bucket 调用。</summary>
    private static long Bucket(long ticks, string granularity, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(new DateTimeOffset(ticks, TimeSpan.Zero), zone);
        if (granularity == "hour") return new DateTimeOffset(local.Year, local.Month, local.Day, local.Hour, 0, 0, local.Offset).UtcTicks;
        var midnight = DateTime.SpecifyKind(local.Date, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(midnight)) midnight = midnight.AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(midnight, zone).Ticks;
    }
}
