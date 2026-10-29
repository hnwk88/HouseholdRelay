using Application.Contracts.Upstream;

namespace Application.Interfaces.Upstream;

/// <summary>活动上游与已保存上游的持久化；所有版本检查都在写事务内完成。</summary>
public interface IUpstreamRepository
{
    /// <summary>读取活动上游原始行（含关联的已保存上游主键与当前名称）；行缺失时为空。</summary>
    StoredUpstream? ReadActive();

    /// <summary>读取活动上游的加密凭据；未配置时为空。</summary>
    byte[]? ReadActiveCredential();

    /// <summary>最近创建的已保存上游主键；一个都没有时为空。</summary>
    string? LatestCandidateId();

    /// <summary>按创建先后列出全部已保存上游的非秘密部分。</summary>
    IReadOnlyList<StoredCandidate> ListCandidates();

    /// <summary>按主键读取已保存上游的非秘密部分；不存在时为空。</summary>
    StoredCandidate? ReadCandidate(string id);

    /// <summary>活动版本仍为 <paramref name="expectedVersion"/> 时写入一个带名称的未测试上游，否则提示重新加载。</summary>
    void InsertCandidate(string id, long expectedVersion, string name, string optionsJson, byte[] protectedCredential);

    /// <summary>只改名称，不动配置、凭据、检测状态和任何按主键关联的记录；不存在时提示重新加载。</summary>
    void Rename(string id, string name);

    /// <summary>删除一个已保存上游；它是活动上游的来源（当前使用中）或正在检测（两分钟内）时拒绝，不存在时提示重新加载。</summary>
    void DeleteCandidate(string id);

    /// <summary>写回获取模型或改选模型后的配置与报告（报告可为空），并把状态置为未测试；不存在时提示重新加载。</summary>
    void SaveUntested(string id, string optionsJson, string? reportJson);

    /// <summary>把候选标记为检测中并返回其加密凭据；候选正在检测（两分钟内）时拒绝。</summary>
    byte[] BeginCandidateTest(string id);

    /// <summary>保存检测结果状态、报告 JSON 与实际采用的配置 JSON。</summary>
    void FinishCandidateTest(string id, string state, string reportJson, string optionsJson);

    /// <summary>
    /// 在一个事务里核对检测结果与活动版本，把检测通过的已保存上游写为新的活动版本并记下它的主键；
    /// <paramref name="expectedVersion"/> 是调用方看到的活动版本，为空时沿用候选保存时基于的版本。
    /// </summary>
    void Activate(string id, long? expectedVersion);
}
