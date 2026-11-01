using Application.Contracts.Upstream;
using Core.Upstream;

namespace Application.Interfaces.Upstream;

/// <summary>隔离启动本机 Claude Code，让它经本机临时端点发出唯一一次候选生成请求。</summary>
public interface IClaudeCodeProbe
{
    /// <summary>本机是否找得到原生 Claude Code；找不到时自动探测不再改用实际客户端测试。</summary>
    bool IsAvailable { get; }

    /// <summary>通过给定连接转发那一次请求并返回上游回复；真实凭据只留在中转进程。发往官方账户时 <paramref name="identity"/> 是统一身份，其余上游为 null。</summary>
    Task<ProbeReply> Run(IUpstreamLease transport, ClaudeCodeIdentity? identity, CancellationToken cancellation);

    /// <summary>让本机 Claude Code 向本机临时端点发一次请求，只记下它带的请求头（不含认证头）就结束；不转发、不碰任何上游。</summary>
    Task<IReadOnlyList<KeyValuePair<string, string>>> Capture(CancellationToken cancellation);
}
