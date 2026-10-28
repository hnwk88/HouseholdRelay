using System.Globalization;
using Microsoft.Data.Sqlite;
using static HouseholdRelay.RelayDatabase;

namespace HouseholdRelay;

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

    internal static UsageReport Read(UsageStore store, UsageFilter filter)
    {
        filter.Validate(); var zone = TimeZoneInfo.FindSystemTimeZoneById(filter.TimeZone);
        var now = store.Now; var cutoff = now.AddDays(-40);
        var from = filter.From is { } since && since > cutoff ? since : cutoff;
        var where = " r.request_started_at >= $from AND r.request_started_at <= $now";
        var parameters = new List<(string Name, object? Value)> { ("$from", from.UtcTicks), ("$now", now.UtcTicks) };
        if (filter.Until is { } until) { where += " AND r.request_started_at < $until"; parameters.Add(("$until", until.UtcTicks)); }
        foreach (var (column, value) in new[] { ("user_id", filter.UserId), ("kind", filter.Kind), ("model", filter.Model), ("outcome", filter.Outcome) })
        {
            if (string.IsNullOrEmpty(value)) continue;
            where += " AND r." + column + "=$" + column; parameters.Add(("$" + column, value));
        }
        using var connection = store.Database.Open(2);
        connection.CreateAggregate<string?, decimal?, string?>("decimal_sum", null,
            (sum, value) => value is null ? sum : (sum ?? 0m) + decimal.Parse(value, CultureInfo.InvariantCulture), UsagePricing.DecimalText);
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
        transaction.Commit();
        return new(cutoff, now, filter.Page, UsageFilter.PageSize, summary.Sum(item => item.Requests), summary, rows, store.PersistenceFailures)
            { Periods = periods, Granularity = filter.Granularity, TimeZone = filter.TimeZone };
    }

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
    private static UsageSummary Summary(SqliteDataReader reader, int offset) => new(reader.GetString(offset), reader.GetInt64(offset + 1), reader.GetInt64(offset + 2), reader.GetInt64(offset + 3),
        reader.GetInt64(offset + 4), reader.GetInt64(offset + 5), reader.GetInt64(offset + 6), reader.GetInt64(offset + 7), reader.GetInt64(offset + 8), Tokens(reader, offset + 9))
        { UnknownAmounts = reader.GetInt64(offset + 13) };
    private static long? Number(SqliteDataReader reader, int column) => reader.IsDBNull(column) ? null : reader.GetInt64(column);
    private static UsageTokens Tokens(SqliteDataReader reader, int offset) => new(Number(reader, offset), Number(reader, offset + 1), Number(reader, offset + 2), Number(reader, offset + 3));
    private static long Bucket(long ticks, string granularity, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(new DateTimeOffset(ticks, TimeSpan.Zero), zone);
        if (granularity == "hour") return new DateTimeOffset(local.Year, local.Month, local.Day, local.Hour, 0, 0, local.Offset).UtcTicks;
        var midnight = DateTime.SpecifyKind(local.Date, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(midnight)) midnight = midnight.AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(midnight, zone).Ticks;
    }
}
