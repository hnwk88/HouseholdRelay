using System.Text.Encodings.Web;
using System.Text.Json;
using Application.Interfaces.Relay;
using Infrastructure.Persistence;

namespace Infrastructure.Logging;

/// <summary>
/// 正式服务的事件输出：每个中转请求结束时一行 JSON，同时写到服务窗口（控制台）和数据目录的 <c>logs\events.log</c>（限 5 MB、留 3 份轮换）。
/// 中文按原样写（宽松编码器），读屏听到的是字；控制台或文件写不出去都不抛出，只计数，已经完成的请求不会因为写日志失败被中止。
/// </summary>
public sealed class ServiceRelayEventLog : IRelayEventLog
{
    /// <summary>序列化选项：宽松编码，中文不转义。</summary>
    private static readonly JsonSerializerOptions _json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly RotatingLogFile _file;
    private readonly TextWriter _console;
    private long _consoleFailures;

    /// <summary>正式服务：写到本进程的控制台和数据目录下的事件日志。</summary>
    public ServiceRelayEventLog(RelayDataOptions data) : this(RelayLogFiles.Events(data.Home), Console.Out) { }

    /// <summary>写到给定的文件和控制台；两者都必填，当场判空。用例用它换上写不进去的控制台。</summary>
    internal ServiceRelayEventLog(RotatingLogFile file, TextWriter console)
    {
        ArgumentNullException.ThrowIfNull(file); ArgumentNullException.ThrowIfNull(console);
        _file = file; _console = console;
    }

    /// <summary>控制台写失败的次数（进程内累计）。</summary>
    public long ConsoleFailures => Interlocked.Read(ref _consoleFailures);

    /// <summary>以 JSON 单行写出事件：先落盘，再写控制台；任何一边失败都只计数。</summary>
    public void Write(object entry)
    {
        var line = JsonSerializer.Serialize(entry, entry.GetType(), _json);
        _file.Append(line);
        try { _console.WriteLine(line); }
        // 服务窗口被关掉、输出被重定向到写满的磁盘：控制台不可写不能让收尾块抛出，请求照常结束，事件已在文件里。
        catch (Exception error) when (error is IOException or ObjectDisposedException or UnauthorizedAccessException) { Interlocked.Increment(ref _consoleFailures); }
    }
}
