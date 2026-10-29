namespace Application.Interfaces.Relay;

/// <summary>中转请求与用量保存失败的结构化事件输出；条目只含编号、路由、状态和耗时，不含令牌或正文。</summary>
public interface IRelayEventLog
{
    /// <summary>输出一条事件。</summary>
    void Write(object entry);
}
