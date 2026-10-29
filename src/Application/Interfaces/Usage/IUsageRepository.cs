using Core.Usage;

namespace Application.Interfaces.Usage;

/// <summary>逐请求用量记录的持久化；每次写入独立短事务提交，不跨越网络等待。</summary>
public interface IUsageRepository
{
    /// <summary>写入请求开始记录，归属取自已鉴权的使用者与令牌。</summary>
    void InsertStart(string requestId, string userId, string? tokenId, long startedAtTicks, string kind);

    /// <summary>把仍处于开始状态的记录标记为已发送，并复制当时的单价快照；返回受影响行数。</summary>
    int MarkDispatched(string requestId, long version, string? model);

    /// <summary>在一个事务里读出单价快照、调用 <paramref name="price"/> 计算金额并写入结束结果；记录已结束时不覆盖。</summary>
    void Complete(string requestId, long endedAtTicks, UsageEnd result, Func<string, string?, UsageMoney> price);

    /// <summary>把所有遗留的开始状态记录标记为中断。</summary>
    void Recover(long nowTicks);

    /// <summary>删除一批（最多 500 行）早于截止点的记录，返回删除行数。</summary>
    int DeleteExpiredBatch(long cutoffTicks);

    /// <summary>按已校验的筛选条件在 40 天窗口内查询明细、汇总、时间段统计与金额合计。</summary>
    UsageReport Query(UsageFilter filter, DateTimeOffset now, long persistenceFailures);
}
