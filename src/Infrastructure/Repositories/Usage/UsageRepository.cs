using Application.Interfaces.Usage;
using Core.Usage;
using Infrastructure.Persistence;
using static Infrastructure.Persistence.RelayDatabase;

namespace Infrastructure.Repositories.Usage;

/// <summary>usage_requests 表的读写；写入使用 1 秒忙等待的独立短连接，查询使用 2 秒。</summary>
internal sealed class UsageRepository(RelayDatabase database) : IUsageRepository
{
    /// <summary>写入请求开始记录。</summary>
    public void InsertStart(string requestId, string userId, string? tokenId, long startedAtTicks, string kind)
    {
        using var connection = database.Open(1);
        Execute(connection, null, """
            INSERT INTO usage_requests(request_id,user_id,token_id,upstream_version,request_started_at,kind)
            VALUES($id,$user,$token,(SELECT version FROM upstream WHERE id=1),$time,$kind)
            """, ("$id", requestId), ("$user", userId), ("$token", tokenId), ("$time", startedAtTicks), ("$kind", kind));
    }

    /// <summary>标记已发送并复制单价快照。</summary>
    public int MarkDispatched(string requestId, long version, string? model)
    {
        using var connection = database.Open(1);
        return Execute(connection, null, """
            UPDATE usage_requests SET upstream_version=$version,model=$model,dispatched=1,
                price_json=(SELECT json FROM usage_prices WHERE upstream_version=$version AND model=$model)
            WHERE request_id=$id AND outcome='started'
            """, ("$id", requestId), ("$version", version), ("$model", model));
    }

    /// <summary>读单价快照、计算金额并写入结束结果。</summary>
    public void Complete(string requestId, long endedAtTicks, UsageEnd result, Func<string, string?, UsageMoney> price)
    {
        using var connection = database.Open(1);
        using var transaction = connection.BeginTransaction();
        string kind; string? priceJson;
        using (var command = Command(connection, transaction, "SELECT outcome,kind,price_json FROM usage_requests WHERE request_id=$id", ("$id", requestId)))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw new InvalidOperationException("Usage start record unavailable.");
            if (reader.GetString(0) != "started") return;
            kind = reader.GetString(1); priceJson = reader.IsDBNull(2) ? null : reader.GetString(2);
        }
        var money = price(kind, priceJson);
        // 条件更新使重复结束通知不覆盖首次已提交结果。
        Execute(connection, transaction, """
            UPDATE usage_requests SET ended_at=$time,outcome=$outcome,dispatched=$sent,status_code=$status,
                upstream_status=$upstream,duration_ms=$duration,completeness=$completeness,
                input_tokens=$input,output_tokens=$output,cache_read_tokens=$read,cache_write_tokens=$write,
                amount=$amount,currency=$currency,money_source=$source,money_completeness=$moneyState
            WHERE request_id=$id AND outcome='started'
            """, ("$id", requestId), ("$time", endedAtTicks), ("$outcome", result.Outcome), ("$sent", result.Dispatched),
            ("$status", result.Status), ("$upstream", result.UpstreamStatus), ("$duration", result.DurationMs),
            ("$completeness", result.Completeness), ("$input", result.Tokens.Input), ("$output", result.Tokens.Output),
            ("$read", result.Tokens.CacheRead), ("$write", result.Tokens.CacheWrite),
            ("$amount", UsageMoneyRules.DecimalText(money.Amount)), ("$currency", money.Currency), ("$source", money.Source), ("$moneyState", money.Completeness));
        transaction.Commit();
    }

    /// <summary>把遗留的开始状态记录标记为中断。</summary>
    public void Recover(long nowTicks)
    {
        using var connection = database.Open(1);
        Execute(connection, null, """
            UPDATE usage_requests SET outcome='interrupted',completeness='partial',money_completeness='partial',ended_at=$time
            WHERE outcome='started'
            """, ("$time", nowTicks));
    }

    /// <summary>删除一批过期记录。</summary>
    public int DeleteExpiredBatch(long cutoffTicks)
    {
        using var connection = database.Open(1);
        return Execute(connection, null, """
            DELETE FROM usage_requests WHERE request_id IN
            (SELECT request_id FROM usage_requests WHERE request_started_at < $cutoff ORDER BY request_started_at LIMIT 500)
            """, ("$cutoff", cutoffTicks));
    }

    /// <summary>查询用量报告。</summary>
    public UsageReport Query(UsageFilter filter, DateTimeOffset now, long persistenceFailures) => UsageQuery.Read(database, filter, now, persistenceFailures);
}
