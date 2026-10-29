using System.Text.Json;
using Application.Interfaces.Relay;

namespace HouseholdRelay.Relay;

/// <summary>正式服务的事件输出：每条事件序列化为一行 JSON 写到控制台。</summary>
public sealed class ConsoleRelayEventLog : IRelayEventLog
{
    /// <summary>以 JSON 单行写出事件。</summary>
    public void Write(object entry) => Console.WriteLine(JsonSerializer.Serialize(entry));
}
