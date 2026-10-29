using Application.Contracts.Upstream;
using Core.Upstream;

namespace Application.Interfaces.Upstream;

/// <summary>独立诊断候选配置：获取模型目录（顺带探测认证方式）、最小调用及 Claude Code 实际会话检测。</summary>
public interface IUpstreamProbe
{
    /// <summary>只读取模型目录，不发模型调用、不花额度；按认证顺序逐个尝试，返回被接受的认证方式与模型清单。</summary>
    Task<ModelCatalog> Discover(UpstreamSnapshot snapshot, CancellationToken cancellation);

    /// <summary>对快照执行一次诊断，返回不含秘密的检测报告与实际采用的配置；不改变活动上游的暂停和冷却状态。</summary>
    Task<ProbeResult> Run(UpstreamSnapshot snapshot, CancellationToken cancellation);
}
