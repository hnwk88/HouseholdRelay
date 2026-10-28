using System.Text.Json;
using static HouseholdRelay.RelayDatabase;

namespace HouseholdRelay;

/// <summary>在同一事务中将基础库升级到候选测试和版本化激活结构。</summary>
internal static class UpstreamSchema
{
    internal static void Upgrade(RelayDatabase database)
    {
        using var connection = database.Open();
        var version = Convert.ToInt32(Scalar(connection, null, "PRAGMA user_version"));
        if (version == 2) return;
        if (version != 1) throw new InvalidOperationException("数据库版本不支持。");
        using var transaction = connection.BeginTransaction();
        var settings = JsonSerializer.Deserialize<RelayConfiguration>((string)Scalar(connection, transaction, "SELECT json FROM settings WHERE id=1")!, ConfigurationFiles.Json)!;
        var baseUrl = (string)Scalar(connection, transaction, "SELECT base_url FROM upstream WHERE id=1")!;
        var options = UpstreamOptions.Legacy(settings, baseUrl);
        Execute(connection, transaction, """
            ALTER TABLE upstream ADD COLUMN options_json TEXT;
            ALTER TABLE upstream ADD COLUMN report_json TEXT;
            CREATE TABLE upstream_candidates (id TEXT PRIMARY KEY, expected_version INTEGER NOT NULL,
                options_json TEXT NOT NULL, credential BLOB NOT NULL, state TEXT NOT NULL, report_json TEXT,
                created_at INTEGER NOT NULL, test_started_at INTEGER);
            PRAGMA user_version=2;
            """);
        Execute(connection, transaction, "UPDATE upstream SET options_json=$json WHERE id=1", ("$json", JsonSerializer.Serialize(options, ConfigurationFiles.Json)));
        transaction.Commit();
    }
}
