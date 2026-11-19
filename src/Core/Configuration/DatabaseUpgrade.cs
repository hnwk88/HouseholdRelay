namespace Core.Configuration;

/// <summary>最近一次数据库架构升级的结果：从几版升到几版、什么时候、升级前备份放在哪、各版迁移另存了哪些数据。存在库的元数据里，重启后照样读得出。</summary>
/// <param name="FromVersion">升级前的库版本。</param>
/// <param name="ToVersion">升级后的库版本。</param>
/// <param name="At">升级完成的时刻（UTC）。</param>
/// <param name="Backup">升级前备份文件相对数据目录的路径，不含本机用户名。</param>
/// <param name="Archived">各版迁移另存的数据，没有另存时为空。</param>
public sealed record DatabaseUpgrade(int FromVersion, int ToVersion, DateTimeOffset At, string Backup, IReadOnlyList<ArchivedRows> Archived)
{
    /// <summary>
    /// 读给管理员的一段话：「数据库已从版本 8 升到 9。升级前的备份在数据目录下 .state\upgrade-backups\…\relay.db。迁移另存在归档表：版本 9：……；……」，
    /// 没有另存时读「迁移没有丢弃或改写任何数据」。
    /// </summary>
    public string Sentence() => $"数据库已从版本 {FromVersion} 升到 {ToVersion}。升级前的备份在数据目录下 {Backup}。"
        + (Archived.Count == 0 ? "迁移没有丢弃或改写任何数据。" : "迁移另存在归档表 migration_archive：" + string.Join("；", Archived.Select(item => item.Sentence())) + "。");
}
