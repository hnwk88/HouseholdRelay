using Core.Upstream;

namespace Application.Interfaces.Upstream;

/// <summary>网页和命令行共用的候选保存、诊断和原子激活服务。</summary>
public interface IUpstreamService : IActiveUpstreamSource
{
    /// <summary>最近保存的候选；没有候选时为空。</summary>
    UpstreamCandidate? LatestCandidate();

    /// <summary>按主键读取候选；不存在时提示重新加载页面。</summary>
    UpstreamCandidate ReadCandidate(string id);

    /// <summary>校验并加密保存候选，返回候选主键；活动版本已变化时拒绝。</summary>
    string SaveCandidate(long expectedVersion, UpstreamOptions options, string credential);

    /// <summary>经操作者确认后对候选做一次最小模型调用检测，并保存安全的检测报告。</summary>
    Task<UpstreamReport> TestCandidate(string id, bool confirmed, CancellationToken cancellation);

    /// <summary>把检测通过的候选原子激活为新的活动版本。</summary>
    void Activate(string id);
}
