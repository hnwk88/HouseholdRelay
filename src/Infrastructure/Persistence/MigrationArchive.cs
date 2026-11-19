using Microsoft.Data.Sqlite;
using static Infrastructure.Persistence.RelayDatabase;

namespace Infrastructure.Persistence;

/// <summary>
/// 迁移归档表 migration_archive 的写入：迁移凡是丢弃、改写或留空的数据，先把原值写成一行 JSON 另存在这里，再动原表。
/// 写入与迁移在同一事务里，迁移回滚时归档一起回滚；每行带本次升级编号，启动结束时按它汇总条数读给管理员。
/// </summary>
internal static class MigrationArchive
{
    /// <summary>建表语句：自增整数主键只做行号，不被任何表引用。</summary>
    private const string Create = """
        CREATE TABLE IF NOT EXISTS migration_archive (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            upgrade_id TEXT NOT NULL,
            schema_version INTEGER NOT NULL,
            source_table TEXT NOT NULL,
            reason TEXT NOT NULL,
            row_json TEXT NOT NULL,
            archived_at INTEGER NOT NULL);
        """;

    /// <summary>表不存在时建表；v4、v6、v9 哪一版先用到就由哪一版建。</summary>
    internal static void Ensure(SqliteConnection connection, SqliteTransaction transaction) => Execute(connection, transaction, Create);

    /// <summary>另存一行：<paramref name="rowJson"/> 是原值的 JSON 文本。</summary>
    internal static void Add(SqliteConnection connection, SqliteTransaction transaction, RelayDatabase database, int schemaVersion, string table, string reason, string rowJson)
    {
        Ensure(connection, transaction);
        Execute(connection, transaction, """
            INSERT INTO migration_archive(upgrade_id,schema_version,source_table,reason,row_json,archived_at)
            VALUES($upgrade,$version,$table,$reason,$json,$now)
            """, ("$upgrade", database.UpgradeId), ("$version", schemaVersion), ("$table", table), ("$reason", reason), ("$json", rowJson),
            ("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
    }

    /// <summary>
    /// 把 <paramref name="select"/> 查出的每一行另存：<paramref name="select"/> 必须只返回一列 JSON 文本（用 SQLite 的 json_object 拼）。返回另存的条数。
    /// </summary>
    internal static int AddRows(SqliteConnection connection, SqliteTransaction transaction, RelayDatabase database, int schemaVersion, string table, string reason, string select)
    {
        Ensure(connection, transaction);
        return Execute(connection, transaction, $"""
            INSERT INTO migration_archive(upgrade_id,schema_version,source_table,reason,row_json,archived_at)
            SELECT $upgrade,$version,$table,$reason,row_json,$now FROM ({select})
            """, ("$upgrade", database.UpgradeId), ("$version", schemaVersion), ("$table", table), ("$reason", reason),
            ("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
    }
}
