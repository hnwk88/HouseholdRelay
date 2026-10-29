using Application.Interfaces.Relay;

namespace Application.Services.Relay;

/// <summary>未指定输出时的事件日志：丢弃所有条目。命令行管理与测试宿主默认使用它，正式服务由组合根换成控制台输出。</summary>
public sealed class NullRelayEventLog : IRelayEventLog
{
    /// <summary>丢弃条目。</summary>
    public void Write(object entry) { }
}
