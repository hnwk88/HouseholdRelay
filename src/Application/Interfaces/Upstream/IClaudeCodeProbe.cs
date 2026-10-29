using Application.Contracts.Upstream;

namespace Application.Interfaces.Upstream;

/// <summary>隔离启动本机 Claude Code，让它经本机临时端点发出唯一一次候选生成请求。</summary>
public interface IClaudeCodeProbe
{
    /// <summary>通过给定连接转发那一次请求并返回上游回复；真实凭据只留在中转进程。</summary>
    Task<ProbeReply> Run(IUpstreamLease transport, CancellationToken cancellation);
}
