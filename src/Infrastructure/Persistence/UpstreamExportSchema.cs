using static Infrastructure.Persistence.RelayDatabase;

namespace Infrastructure.Persistence;

/// <summary>v6 → v7：已保存上游加「导出搬走的时刻」一列；整个升级在一个事务里，失败全部回滚，已有上游都读作没导出过。</summary>
internal sealed class UpstreamExportSchema(RelayDataOptions options) : IDatabaseMigration
{
    /// <summary>排在最后（70）：依赖自动切换升级已把库带到版本 6。</summary>
    public int Order => 70;

    /// <summary>已是 7 时直接返回，不是 6 时拒绝。在一个事务里给 upstream_candidates 加 exported_at（Unix 毫秒，空表示没导出过），再写入版本号 7。</summary>
    public void Apply(RelayDatabase database)
    {
        using var connection = database.Open();
        var version = Convert.ToInt32(Scalar(connection, null, "PRAGMA user_version"));
        if (version == 7) return;
        if (version != 6) throw new InvalidOperationException("数据库版本不支持。");
        using var transaction = connection.BeginTransaction();
        // 版本号被改回 6 的库（备份恢复或用例模拟旧库）里新列可能已经在了，重放时跳过，不报重复列。
        if (Convert.ToInt32(Scalar(connection, transaction, "SELECT COUNT(*) FROM pragma_table_info('upstream_candidates') WHERE name='exported_at'")) == 0)
            Execute(connection, transaction, "ALTER TABLE upstream_candidates ADD COLUMN exported_at INTEGER");
        options.MigrationCheckpoint?.Invoke();
        Execute(connection, transaction, "PRAGMA user_version=7");
        transaction.Commit();
    }
}
