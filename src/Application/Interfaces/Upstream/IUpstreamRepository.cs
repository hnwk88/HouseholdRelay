using Application.Contracts.Upstream;

namespace Application.Interfaces.Upstream;

/// <summary>活动上游与已保存上游的持久化；所有版本检查都在写事务内完成。</summary>
public interface IUpstreamRepository
{
    /// <summary>读取活动上游原始行（含关联的已保存上游主键与当前名称）；行缺失时为空。</summary>
    StoredUpstream? ReadActive();

    /// <summary>读取在用那一家以外、测试通过且没有导出搬走的已保存上游（含加密凭据），按创建先后；版本记为此刻的活动版本。</summary>
    IReadOnlyList<StoredUpstream> ListStandby();

    /// <summary>打开或关闭自动切换；只改活动上游行上的开关，不改版本、不换在用的那一家。</summary>
    void SetFailover(bool enabled);

    /// <summary>读取活动上游的加密凭据；未配置时为空。</summary>
    byte[]? ReadActiveCredential();

    /// <summary>最近创建的已保存上游主键；一个都没有时为空。</summary>
    long? LatestCandidateId();

    /// <summary>按创建先后列出全部已保存上游的非秘密部分。</summary>
    IReadOnlyList<StoredCandidate> ListCandidates();

    /// <summary>按主键读取已保存上游的非秘密部分；不存在时为空。</summary>
    StoredCandidate? ReadCandidate(long id);

    /// <summary>按主键读取已保存上游的加密凭据（导出用）；不存在时提示重新加载。</summary>
    byte[] ReadCandidateCredential(long id);

    /// <summary>在一个写事务里给这些已保存上游记下导出搬走的时刻（已记过的保留原时刻）；有一个不存在就整批不记，提示重新加载。</summary>
    void MarkExported(IReadOnlyCollection<long> ids);

    /// <summary>活动版本仍为 <paramref name="expectedVersion"/> 时写入一个带名称的未测试上游并返回库生成的主键，否则提示重新加载；<paramref name="uid"/> 是保存前生成的对外编号（官方账户目录已按它建好）。</summary>
    long InsertCandidate(string uid, long expectedVersion, string name, string optionsJson, byte[] protectedCredential);

    /// <summary>只改名称，不动配置、凭据、检测状态和任何按主键关联的记录；不存在时提示重新加载。</summary>
    void Rename(long id, string name);

    /// <summary>
    /// 停用（软删除）一个已保存上游：记下停用时刻、清掉加密凭据，此后它对本接口其余方法都等于不存在，只在用量历史里读得出名字。
    /// 只在它此刻仍未停用、不是活动上游的来源、也不在检测窗口内时才写；同一事务里数出仍引用它的用量、尝试和单价条数返回。
    /// 条件不满足（刚被切换过去、刚开始检测、已停用或不存在）时什么都不写，返回 null，由调用方读出原因。
    /// </summary>
    UpstreamReferences? Deactivate(long id);

    /// <summary>写回获取模型或改选模型后的配置与报告（报告可为空），并把状态置为未测试；不存在时提示重新加载。</summary>
    void SaveUntested(long id, string optionsJson, string? reportJson);

    /// <summary>把候选标记为检测中并返回其加密凭据；候选正在检测（两分钟内）时拒绝。</summary>
    byte[] BeginCandidateTest(long id);

    /// <summary>保存检测结果状态、报告 JSON 与实际采用的配置 JSON。</summary>
    void FinishCandidateTest(long id, string state, string reportJson, string optionsJson);

    /// <summary>
    /// 在一个事务里核对检测结果与活动版本，把检测通过的已保存上游写为新的活动版本并记下它的主键；
    /// <paramref name="expectedVersion"/> 是调用方看到的活动版本，为空时沿用候选保存时基于的版本。
    /// </summary>
    void Activate(long id, long? expectedVersion);
}
