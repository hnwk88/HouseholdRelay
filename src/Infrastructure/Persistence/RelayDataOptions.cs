namespace Infrastructure.Persistence;

/// <summary>数据目录启动参数（命令行 --home，默认程序所在目录），由组合根以单例选项注入。</summary>
public sealed class RelayDataOptions
{
    /// <summary>数据目录；数据库、实例标记和受保护状态文件夹都在其下。</summary>
    public string Home { get; set; } = AppContext.BaseDirectory;

    /// <summary>仅供测试：旧数据导入提交前调用，用来注入故障验证整体回滚。</summary>
    internal Action? MigrationCheckpoint { get; set; }
}
