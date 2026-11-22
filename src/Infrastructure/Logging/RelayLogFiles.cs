using Microsoft.Extensions.Logging;

namespace Infrastructure.Logging;

/// <summary>
/// 数据目录下的两种日志文件：<c>logs\errors.log</c>（兜底异常与框架警告，只有类型、阶段、编号）与 <c>logs\events.log</c>（每个中转请求一行事件）。
/// 都是 <see cref="RotatingLogFile"/>，同一路径进程内共用一份；也负责把文件日志挂进各个宿主与命令行容器的日志管道。
/// </summary>
public static class RelayLogFiles
{
    /// <summary>日志文件夹：数据目录下的 logs。</summary>
    /// <param name="home">数据目录。</param>
    public static string Folder(string home) => Path.Combine(Path.GetFullPath(home), "logs");

    /// <summary>错误日志：兜底异常与框架警告。</summary>
    /// <param name="home">数据目录。</param>
    public static RotatingLogFile Errors(string home) => RotatingLogFile.For(Path.Combine(Folder(home), "errors.log"));

    /// <summary>事件日志：每个中转请求结束时一行。</summary>
    /// <param name="home">数据目录。</param>
    public static RotatingLogFile Events(string home) => RotatingLogFile.For(Path.Combine(Folder(home), "events.log"));

    /// <summary>
    /// 把数据目录 <paramref name="home"/> 的错误日志挂进这条日志管道，最低级别定为 Warning；不清空已有的提供程序。
    /// 文件日志收全部警告与错误（它自己只写类型、阶段、编号）；控制台、Windows 事件日志等框架默认的提供程序只收本工程自己的类别——
    /// 框架的日志消息里带 SQL、文件路径（含用户名）与堆栈，不打到服务窗口、不进系统日志。
    /// </summary>
    /// <param name="logging">日志管道。</param>
    /// <param name="home">数据目录。</param>
    public static ILoggingBuilder AddRelayFileLog(this ILoggingBuilder logging, string home)
    {
        logging.AddProvider(new FileLoggerProvider(Errors(home)));
        logging.SetMinimumLevel(LogLevel.Warning);
        logging.AddFilter((provider, category, level) => level >= LogLevel.Warning && (provider == typeof(FileLoggerProvider).FullName || Own(category)));
        // Windows 事件日志在宿主默认登记时自带一条按提供程序的规则，比上面那条更具体，要单独再给一条才压得住。
        return logging.AddFilter<Microsoft.Extensions.Logging.EventLog.EventLogLoggerProvider>((category, level) => level >= LogLevel.Warning && Own(category));
    }

    /// <summary>本工程自己的日志类别（不是 Microsoft.*、System.* 框架类别）：消息由 <c>Application.Logging.RelayLog</c> 拼，只有类型、阶段、编号。</summary>
    private static bool Own(string? category) =>
        category is not null && !category.StartsWith("Microsoft.", StringComparison.Ordinal) && !category.StartsWith("System.", StringComparison.Ordinal);
}
