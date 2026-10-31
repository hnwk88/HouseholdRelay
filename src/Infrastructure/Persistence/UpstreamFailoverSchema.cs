using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using static Infrastructure.Persistence.RelayDatabase;

namespace Infrastructure.Persistence;

/// <summary>v5 → v6：自动切换开关、用量记实际服务那一家、单价改按已保存上游主键；整个升级在一个事务里，失败全部回滚，已有用量不动。</summary>
internal sealed class UpstreamFailoverSchema(RelayDataOptions options) : IDatabaseMigration
{
    /// <summary>排在最后（60）：依赖尝试记录升级已把库带到版本 5。</summary>
    public int Order => 60;

    /// <summary>
    /// 已是 6 或更新时直接返回，不是 5 时拒绝。在一个事务里：活动上游行加 failover 列（默认开）；用量记录加 candidate_id 列，按已有尝试记录回填最后一次尝试的上游；
    /// 单价表重建为按（已保存上游主键, 模型）唯一，旧行里属于当前活动版本的改挂到当前在用的那一家，更早版本的单价此后不会再被查到，不迁。最后写入版本号 6。
    /// </summary>
    public void Apply(RelayDatabase database)
    {
        using var connection = database.Open();
        var version = Convert.ToInt32(Scalar(connection, null, "PRAGMA user_version"));
        if (version is 6 or 7) return;
        if (version != 5) throw new InvalidOperationException("数据库版本不支持。");
        using var transaction = connection.BeginTransaction();
        // 每一步先看是不是已经是新形状：版本号被改回 5 的库（备份恢复或用例模拟旧库）里新列可能已经在了，重放时跳过那一步，不报重复列。
        if (!HasColumn(connection, transaction, "upstream", "failover"))
            Execute(connection, transaction, "ALTER TABLE upstream ADD COLUMN failover INTEGER NOT NULL DEFAULT 1 CHECK(failover IN (0,1))");
        if (!HasColumn(connection, transaction, "usage_requests", "candidate_id"))
            Execute(connection, transaction, """
                ALTER TABLE usage_requests ADD COLUMN candidate_id TEXT;
                UPDATE usage_requests SET candidate_id=(SELECT a.candidate_id FROM usage_attempts a WHERE a.request_id=usage_requests.request_id ORDER BY a.attempt DESC LIMIT 1);
                """);
        if (HasColumn(connection, transaction, "usage_prices", "upstream_version")) RekeyPrices(connection, transaction);
        options.MigrationCheckpoint?.Invoke();
        Execute(connection, transaction, "PRAGMA user_version=6");
        transaction.Commit();
    }

    /// <summary>单价表重建为按（已保存上游主键, 模型）唯一：属于当前活动版本的旧行改挂到当前在用的那一家，其余不迁。</summary>
    private static void RekeyPrices(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, transaction, "CREATE TABLE usage_prices_v6(id TEXT PRIMARY KEY,candidate_id TEXT NOT NULL,model TEXT NOT NULL,json TEXT NOT NULL,UNIQUE(candidate_id,model))");
        var candidate = Scalar(connection, transaction, "SELECT candidate_id FROM upstream WHERE id=1") as string;
        var prices = new List<(string Id, string Model, string Json)>();
        using (var command = Command(connection, transaction, "SELECT id,model,json FROM usage_prices WHERE upstream_version=(SELECT version FROM upstream WHERE id=1)"))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) prices.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        if (candidate is not null)
            foreach (var (id, model, json) in prices)
                Execute(connection, transaction, "INSERT INTO usage_prices_v6(id,candidate_id,model,json) VALUES($id,$candidate,$model,$json)",
                    ("$id", id), ("$candidate", candidate), ("$model", model), ("$json", Rekey(json, candidate)));
        Execute(connection, transaction, "DROP TABLE usage_prices; ALTER TABLE usage_prices_v6 RENAME TO usage_prices;");
    }

    /// <summary>表里有没有这一列。</summary>
    private static bool HasColumn(SqliteConnection connection, SqliteTransaction transaction, string table, string column) =>
        Convert.ToInt32(Scalar(connection, transaction, "SELECT COUNT(*) FROM pragma_table_info($table) WHERE name=$column", ("$table", table), ("$column", column))) != 0;

    /// <summary>把单价 JSON 里的活动版本号换成已保存上游主键；其余字段原样保留。</summary>
    private static string Rekey(string json, string candidate)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        node.Remove("UpstreamVersion"); node["CandidateId"] = candidate;
        return node.ToJsonString();
    }
}
