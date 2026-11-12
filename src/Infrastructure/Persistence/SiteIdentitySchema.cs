using static Infrastructure.Persistence.RelayDatabase;

namespace Infrastructure.Persistence;

/// <summary>v7 → v8：增加站点身份代次和维护幂等历史，不复制 settings 中的设备编号。</summary>
internal sealed class SiteIdentitySchema(RelayDataOptions options) : IDatabaseMigration
{
    /// <summary>既有业务表迁移完成后再创建身份状态。</summary>
    public int Order => 80;

    /// <summary>合法旧站从第一代 ready 开始；事务中断保留原库，已经升级的状态缺失则拒绝修补。</summary>
    public void Apply(RelayDatabase database)
    {
        using var connection = database.Open();
        var version = Convert.ToInt32(Scalar(connection, null, "PRAGMA user_version"));
        if (version == 8)
        {
            if (Convert.ToInt32(Scalar(connection, null, "SELECT COUNT(*) FROM site_identity WHERE id=1")) != 1)
                throw new InvalidOperationException("站点身份状态缺失，请恢复配套备份。");
            return;
        }
        if (version != 7) throw new InvalidOperationException("数据库版本不支持。");
        using var transaction = connection.BeginTransaction();
        _ = ReadSettings(connection, transaction);
        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS site_identity_operations (
                operation_id TEXT PRIMARY KEY, version INTEGER NOT NULL UNIQUE CHECK(version >= 2),
                pending INTEGER NOT NULL CHECK(pending IN (0,1)));
            CREATE TABLE IF NOT EXISTS site_identity (
                id INTEGER PRIMARY KEY CHECK(id=1), version INTEGER NOT NULL CHECK(version >= 1),
                operation_id TEXT REFERENCES site_identity_operations(operation_id),
                pending INTEGER NOT NULL CHECK(pending IN (0,1)));
            INSERT OR IGNORE INTO site_identity VALUES(1,1,NULL,0);
            """);
        options.MigrationCheckpoint?.Invoke();
        Execute(connection, transaction, "PRAGMA user_version=8");
        transaction.Commit();
    }
}
