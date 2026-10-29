using Application.Contracts.Upstream;

namespace Application.Interfaces.Upstream;

/// <summary>隔离启动本机 Claude Code，让它经本机临时端点发出唯一一次候选生成请求。</summary>
public interface IClaudeCodeProbe
{
    /// <summary>本机是否找得到原生 Claude Code；找不到时自动探测不再改用实际客户端测试。</summary>
    bool IsAvailable { get; }

    /// <summary>通过给定连接转发那一次请求并返回上游回复；真实凭据只留在中转进程。</summary>
    Task<ProbeReply> Run(IUpstreamLease transport, CancellationToken cancellation);
}
