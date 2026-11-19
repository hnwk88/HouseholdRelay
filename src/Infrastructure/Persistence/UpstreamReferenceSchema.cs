using Microsoft.Data.Sqlite;
using static Infrastructure.Persistence.RelayDatabase;

namespace Infrastructure.Persistence;

/// <summary>
/// v8 → v9：已保存上游改成停用（软删除）；用量、上游尝试、单价三张表对已保存上游补外键；用量与尝试加记账那一刻的上游名称快照。
/// 整个升级在一个事务里，失败全部回滚；指向已不存在上游的数据先另存进归档表再处理，条数由启动结束时读给管理员。
/// </summary>
internal sealed class UpstreamReferenceSchema(RelayDataOptions options) : IDatabaseMigration
{
    /// <summary>本版的库版本号。</summary>
    private const int Version = 9;

    /// <summary>新表定义：列顺序与旧表一致，末尾加名称快照；外键不级联，上游只停用不删除，引用永远有效。</summary>
    private static readonly (string Table, string Create)[] _tables =
    [
        ("usage_prices", """
            CREATE TABLE usage_prices_v9(id TEXT PRIMARY KEY,candidate_id TEXT NOT NULL REFERENCES upstream_candidates(id),
                model TEXT NOT NULL,json TEXT NOT NULL,UNIQUE(candidate_id,model))
            """),
        ("usage_requests", """
            CREATE TABLE usage_requests_v9 (
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
                money_source TEXT NOT NULL DEFAULT 'unknown', money_completeness TEXT NOT NULL DEFAULT 'unknown',
                candidate_id TEXT REFERENCES upstream_candidates(id),
                upstream_name TEXT)
            """),
        ("usage_attempts", """
            CREATE TABLE usage_attempts_v9 (
                request_id TEXT NOT NULL REFERENCES usage_requests(request_id) ON DELETE CASCADE,
                attempt INTEGER NOT NULL CHECK(attempt >= 1),
                candidate_id TEXT REFERENCES upstream_candidates(id),
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
                upstream_name TEXT,
                PRIMARY KEY(request_id, attempt))
            """),
    ];

    /// <summary>排在最后（90）：依赖站点身份升级已把库带到版本 8。</summary>
    public int Order => 90;

    /// <summary>
    /// 已是 9 或更新时直接返回，不是 8 时拒绝。照 SQLite「ALTER TABLE 12 步」：事务外关外键检查，事务内建新表、按新旧共同列搬数据、删旧表、改名、
    /// 照原样重建旧表上的索引，再对三张表做外键检查，有一条不过就回滚；提交后连接释放，下一个连接照常开外键检查。
    /// 指向已不存在上游的单价整行另存、不搬；用量、尝试和活动行里的悬空引用原值另存、引用置空，记录本身保留。
    /// </summary>
    public void Apply(RelayDatabase database)
    {
        using var connection = database.Open();
        var version = Convert.ToInt32(Scalar(connection, null, "PRAGMA user_version"));
        if (version >= Version) return;
        if (version != 8) throw new InvalidOperationException("数据库版本不支持。");
        // 外键开关在事务里设置无效，必须在开事务前关；否则删旧用量表会按 ON DELETE CASCADE 连带删光尝试记录。
        Execute(connection, null, "PRAGMA foreign_keys=OFF");
        using var transaction = connection.BeginTransaction();
        MigrationArchive.Ensure(connection, transaction);
        // 每一步先看是不是已经是新形状：版本号被改回 8 的库（备份恢复或用例模拟旧库）里新列可能已经在了，重放时跳过那一步。
        if (!HasColumn(connection, transaction, "upstream_candidates", "deleted_at"))
            Execute(connection, transaction, "ALTER TABLE upstream_candidates ADD COLUMN deleted_at INTEGER");
        const string Missing = "candidate_id IS NOT NULL AND candidate_id NOT IN (SELECT id FROM upstream_candidates)";
        MigrationArchive.AddRows(connection, transaction, database, Version, "upstream", "orphan-reference",
            $"SELECT json_object('id',id,'candidate_id',candidate_id) AS row_json FROM upstream WHERE {Missing}");
        Execute(connection, transaction, $"UPDATE upstream SET candidate_id=NULL WHERE {Missing}");
        MigrationArchive.AddRows(connection, transaction, database, Version, "usage_prices", "orphan-price",
            $"SELECT json_object('id',id,'candidate_id',candidate_id,'model',model,'json',json) AS row_json FROM usage_prices WHERE {Missing}");
        MigrationArchive.AddRows(connection, transaction, database, Version, "usage_requests", "orphan-reference",
            $"SELECT json_object('request_id',request_id,'candidate_id',candidate_id) AS row_json FROM usage_requests WHERE {Missing}");
        MigrationArchive.AddRows(connection, transaction, database, Version, "usage_attempts", "orphan-reference",
            $"SELECT json_object('request_id',request_id,'attempt',attempt,'candidate_id',candidate_id) AS row_json FROM usage_attempts WHERE {Missing}");
        var indexes = new List<string>();
        using (var command = Command(connection, transaction,
            "SELECT sql FROM sqlite_master WHERE type='index' AND sql IS NOT NULL AND tbl_name IN ('usage_prices','usage_requests','usage_attempts') ORDER BY name"))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) indexes.Add(reader.GetString(0));
        foreach (var (table, create) in _tables)
        {
            Execute(connection, transaction, create);
            var columns = string.Join(",", Columns(connection, transaction, table).Intersect(Columns(connection, transaction, table + "_v9")));
            Execute(connection, transaction, $"INSERT INTO {table}_v9({columns}) SELECT {columns} FROM {table}" + (table == "usage_prices" ? $" WHERE NOT ({Missing})" : ""));
            if (table != "usage_prices") Execute(connection, transaction, $"UPDATE {table}_v9 SET candidate_id=NULL WHERE {Missing}");
        }
        // 先删子表再删父表；改名后尝试表的外键按名字指回新的用量表。
        Execute(connection, transaction, """
            DROP TABLE usage_attempts; DROP TABLE usage_requests; DROP TABLE usage_prices;
            ALTER TABLE usage_prices_v9 RENAME TO usage_prices;
            ALTER TABLE usage_requests_v9 RENAME TO usage_requests;
            ALTER TABLE usage_attempts_v9 RENAME TO usage_attempts;
            """);
        foreach (var index in indexes) Execute(connection, transaction, index);
        foreach (var (table, _) in _tables)
            if (Scalar(connection, transaction, $"SELECT COUNT(*) FROM pragma_foreign_key_check('{table}')") is long broken && broken != 0)
                throw new InvalidOperationException($"升级到版本 {Version} 后 {table} 表仍有 {broken} 条外键对不上，已整体回滚，请保留数据目录并联系维护者。");
        options.MigrationCheckpoint?.Invoke();
        Execute(connection, transaction, $"PRAGMA user_version={Version}");
        transaction.Commit();
    }

    /// <summary>按表定义顺序列出列名。</summary>
    private static List<string> Columns(SqliteConnection connection, SqliteTransaction transaction, string table)
    {
        using var command = Command(connection, transaction, "SELECT name FROM pragma_table_info($table) ORDER BY cid", ("$table", table));
        using var reader = command.ExecuteReader(); var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }

    /// <summary>表里有没有这一列。</summary>
    private static bool HasColumn(SqliteConnection connection, SqliteTransaction transaction, string table, string column) =>
        Convert.ToInt32(Scalar(connection, transaction, "SELECT COUNT(*) FROM pragma_table_info($table) WHERE name=$column", ("$table", table), ("$column", column))) != 0;
}
