using System.Text.Encodings.Web;
using System.Text.Json;
using Core.Relay;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Logging;

/// <summary>
/// 写进滚动文件的日志：每条一行 JSON，只写时刻、级别、类别、事件名，加上白名单里的字段（异常类型、阶段、请求编号、上游主键；思域打回留痕的使用者、令牌主键、会话编号、原因码、下一步、身份代次）与异常类型链。
/// 不写格式化后的消息、不写异常消息与堆栈、不写白名单外的任何参数：框架自己的日志消息里可能带地址、路径或请求内容，这里一概不落盘。
/// 中文按原样写（宽松编码器），读屏打开文件听到的是字，不是一串反斜杠。只收 Warning 及以上。
/// </summary>
internal sealed class FileLogger(RotatingLogFile file, string category) : ILogger
{
    /// <summary>只认这几个参数名；本工程自己的日志事件（<c>Application.Logging.RelayLog</c>）的模板参数就叫这几个名字。全是主键、编号与固定码，没有秘密。</summary>
    private static readonly Dictionary<string, string> _fields = new(StringComparer.Ordinal)
    {
        ["ExceptionType"] = "exceptionType", ["Stage"] = "stage", ["RequestId"] = "requestId", ["UpstreamCandidateId"] = "upstreamCandidateId",
        ["UserId"] = "userId", ["TokenId"] = "tokenId", ["SessionId"] = "sessionId", ["Code"] = "code", ["Remedy"] = "remedy", ["IdentityVersion"] = "identityVersion",
    };

    /// <summary>序列化选项：宽松编码，中文不转义。</summary>
    private static readonly JsonSerializerOptions _json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>不支持作用域：作用域里的值不在白名单里，也不写。</summary>
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    /// <summary>只收 Warning 及以上。</summary>
    public bool IsEnabled(LogLevel logLevel) => logLevel is >= LogLevel.Warning and < LogLevel.None;

    /// <summary>按白名单拼成一行 JSON 追加到文件；<paramref name="formatter"/> 不调用，消息正文不落盘。</summary>
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        var entry = new Dictionary<string, object?>
        {
            ["time"] = DateTimeOffset.UtcNow, ["level"] = logLevel.ToString(), ["category"] = category,
            ["event"] = eventId.Name ?? eventId.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        if (state is IEnumerable<KeyValuePair<string, object?>> values)
            foreach (var (key, value) in values)
                if (_fields.TryGetValue(key, out var name)) entry[name] = value is null or string or long or int ? value : value.ToString();
        if (exception is not null) entry["exceptionType"] = ExceptionChain.Types(exception);
        file.Append(JsonSerializer.Serialize(entry, _json));
    }
}
