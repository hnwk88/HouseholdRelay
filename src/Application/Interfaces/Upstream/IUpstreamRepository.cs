using Application.Contracts.Upstream;

namespace Application.Interfaces.Upstream;

/// <summary>活动上游与候选上游的持久化；所有版本检查都在写事务内完成。</summary>
public interface IUpstreamRepository
{
    /// <summary>读取活动上游原始行；行缺失时为空。</summary>
    StoredUpstream? ReadActive();

    /// <summary>读取活动上游的加密凭据；未配置时为空。</summary>
    byte[]? ReadActiveCredential();

    /// <summary>最近创建的候选主键；没有候选时为空。</summary>
    string? LatestCandidateId();

    /// <summary>按主键读取候选的非秘密部分；不存在时为空。</summary>
    StoredCandidate? ReadCandidate(string id);

    /// <summary>活动版本仍为 <paramref name="expectedVersion"/> 时写入未测试候选，否则提示重新加载。</summary>
    void InsertCandidate(string id, long expectedVersion, string optionsJson, byte[] protectedCredential);

    /// <summary>把候选标记为检测中并返回其加密凭据；候选正在检测（两分钟内）时拒绝。</summary>
    byte[] BeginCandidateTest(string id);

    /// <summary>保存检测结果状态与报告 JSON。</summary>
    void FinishCandidateTest(string id, string state, string reportJson);

    /// <summary>在一个事务里核对检测结果与活动版本，把候选写为新的活动版本。</summary>
    void Activate(string id);
}
