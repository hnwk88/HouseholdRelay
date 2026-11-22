using Microsoft.Extensions.Logging;

namespace Infrastructure.Logging;

/// <summary>
/// 文件日志提供程序：把 Warning 及以上的日志按 <see cref="FileLogger"/> 的白名单写进一份 <see cref="RotatingLogFile"/>。
/// 框架没有现成的文件日志（只有控制台、调试、EventSource 与 Windows 事件日志，后者原样记消息正文），所以写这个最小的；不引第三方日志包。
/// 文件归 <see cref="RotatingLogFile.For"/> 的共用表所有，宿主释放提供程序时不关文件。
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly RotatingLogFile _file;

    /// <summary>写进 <paramref name="file"/>；必填，当场判空。</summary>
    public FileLoggerProvider(RotatingLogFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        _file = file;
    }

    /// <summary>按类别建一个写这份文件的日志。</summary>
    public ILogger CreateLogger(string categoryName) => new FileLogger(_file, categoryName);

    /// <summary>文件不归提供程序所有，这里什么都不做。</summary>
    public void Dispose() { }
}
