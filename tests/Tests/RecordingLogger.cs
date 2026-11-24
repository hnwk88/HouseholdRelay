using Microsoft.Extensions.Logging;

namespace Tests;

/// <summary>用例用的日志替身：记下每条日志的级别、事件名与结构化参数（不格式化消息），供核对留痕字段。</summary>
public sealed class RecordingLogger<T> : ILogger<T>
{
    /// <summary>记下的日志，按先后。</summary>
    public List<(LogLevel Level, string? Event, IReadOnlyDictionary<string, object?> Fields)> Entries { get; } = [];

    /// <summary>不支持作用域。</summary>
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    /// <summary>全部级别都收。</summary>
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <summary>只取结构化参数，不调用格式化器。</summary>
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var fields = state is IEnumerable<KeyValuePair<string, object?>> values ? values.ToDictionary(pair => pair.Key, pair => pair.Value) : [];
        // 用例可能并发写；同锁追加。
        lock (Entries) Entries.Add((logLevel, eventId.Name, fields));
    }
}
