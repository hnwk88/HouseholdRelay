using static Infrastructure.Persistence.RelayDatabase;

namespace Infrastructure.Persistence;

/// <summary>逐请求用量与配置共库，迁移和索引创建在一个短事务中完成。</summary>
internal sealed class UsageSchema : IDatabaseMigration
{
    /// <summary>排在最后（30）：依赖上游架构升级已把库带到版本 2。</summary>
    public int Order => 30;

    /// <summary>把库升级到版本 3：已是 3 及以上时直接返回；否则在一个事务里建立逐请求用量表、按上游版本和模型的价格表及按时间、用户、类型、模型、结果查询的索引，并写入新版本号。</summary>
    public void Apply(RelayDatabase database)
    {
        using var connection = database.Open();
        if (Convert.ToInt32(Scalar(connection, null, "PRAGMA user_version")) >= 3) return;
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, """
            CREATE TABLE usage_requests (
                request_id TEXT PRIMARY KEY,
                user_id TEXT NOT NULL REFERENCES users(id),
                token_id TEXT NOT NULL REFERENCES tokens(id),
                upstream_version INTEGER,
                request_started_at INTEGER NOT NULL,
                ended_at INTEGER,
                kind TEXT NOT NULL CHECK(kind IN ('inference','estimate','models')),
                model TEXT,
                outcome TEXT NOT NULL DEFAULT 'started',
                dispatched INTEGER NOT NULL DEFAULT 0,
                status_code INTEGER,
                upstream_status INTEGER,
                duration_ms INTEGER,
                completeness TEXT NOT NULL DEFAULT 'partial',
                input_tokens INTEGER, output_tokens INTEGER,
                cache_read_tokens INTEGER, cache_write_tokens INTEGER,
                price_json TEXT, amount TEXT, currency TEXT,
                money_source TEXT NOT NULL DEFAULT 'unknown', money_completeness TEXT NOT NULL DEFAULT 'unknown');
            CREATE TABLE usage_prices(id TEXT PRIMARY KEY,upstream_version INTEGER NOT NULL,model TEXT NOT NULL,json TEXT NOT NULL,
                UNIQUE(upstream_version,model));
            CREATE INDEX usage_time ON usage_requests(request_started_at,request_id);
            CREATE INDEX usage_user_time ON usage_requests(user_id,request_started_at,request_id);
            CREATE INDEX usage_kind_time ON usage_requests(kind,request_started_at);
            CREATE INDEX usage_model_time ON usage_requests(model,request_started_at);
            CREATE INDEX usage_outcome_time ON usage_requests(outcome,request_started_at);
            PRAGMA user_version=3;
            """);
        transaction.Commit();
    }
}
