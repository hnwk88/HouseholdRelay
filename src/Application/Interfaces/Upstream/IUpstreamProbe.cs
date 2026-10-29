using Core.Upstream;

namespace Application.Interfaces.Upstream;

/// <summary>独立诊断候选配置：模型目录、最小调用及可选的 Claude Code 实际会话检测。</summary>
public interface IUpstreamProbe
{
    /// <summary>对快照执行一次诊断，返回不含秘密的检测报告；不改变活动上游的暂停和冷却状态。</summary>
    Task<UpstreamReport> Run(UpstreamSnapshot snapshot, CancellationToken cancellation);
}
