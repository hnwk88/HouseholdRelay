using static Infrastructure.Persistence.RelayDatabase;

namespace Infrastructure.Persistence;

/// <summary>v4 → v5：建立每次上游尝试的记录表，挂在用量记录下随它一起清理；整个升级在一个事务里，失败全部回滚，已有用量不动。</summary>
internal sealed class UsageAttemptSchema(RelayDataOptions options) : IDatabaseMigration
{
    /// <summary>排在最后（50）：依赖上游命名升级已把库带到版本 4。</summary>
    public int Order => 50;

    /// <summary>
    /// 已是 5 时直接返回，不是 4 时拒绝。在一个事务里建 usage_attempts：按请求编号与第几次定主键，外键指向用量记录并级联删除，
    /// 所以 40 天清理删掉用量时同一条语句就带走它的尝试；上游候选只记主键不设外键，候选删除后历史仍在、名称读作已删除。最后写入版本号 5。
    /// </summary>
    public void Apply(RelayDatabase database)
    {
        using var connection = database.Open();
        var version = Convert.ToInt32(Scalar(connection, null, "PRAGMA user_version"));
        if (version is 5 or 6) return;
        if (version != 4) throw new InvalidOperationException("数据库版本不支持。");
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, """
            CREATE TABLE usage_attempts (
                request_id TEXT NOT NULL REFERENCES usage_requests(request_id) ON DELETE CASCADE,
                attempt INTEGER NOT NULL CHECK(attempt >= 1),
                candidate_id TEXT,
                upstream_version INTEGER NOT NULL,
                started_at INTEGER NOT NULL,
                headers_ms INTEGER,
                duration_ms INTEGER NOT NULL,
                upstream_status INTEGER,
                upstream_request_id TEXT,
                error_type TEXT,
                error_message TEXT,
                connect_failure TEXT,
                disposition TEXT NOT NULL CHECK(disposition IN ('client','next')),
                PRIMARY KEY(request_id, attempt));
            CREATE INDEX usage_attempts_candidate ON usage_attempts(candidate_id,started_at);
            """);
        options.MigrationCheckpoint?.Invoke();
        Execute(connection, transaction, "PRAGMA user_version=5");
        transaction.Commit();
    }
}
