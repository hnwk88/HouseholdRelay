using System.Text.Json;
using Core.Configuration;
using Core.Upstream;
using static Infrastructure.Persistence.RelayDatabase;

namespace Infrastructure.Persistence;

/// <summary>在同一事务中将基础库升级到候选测试和版本化激活结构。</summary>
internal sealed class UpstreamSchema : IDatabaseMigration
{
    /// <summary>排在第二位（20）：在旧数据导入之后、用量架构升级之前执行。</summary>
    public int Order => 20;

    /// <summary>把版本 1 的库升级到版本 2：已是 2 及以上时直接返回，其他版本拒绝；在一个事务里给上游表加上选项和报告列、建立候选配置表，并把旧设置与地址换算成上游选项 JSON 写回，失败则整体回滚。</summary>
    public void Apply(RelayDatabase database)
    {
        using var connection = database.Open();
        var version = Convert.ToInt32(Scalar(connection, null, "PRAGMA user_version"));
        if (version is 2 or 3 or 4 or 5 or 6) return;
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
