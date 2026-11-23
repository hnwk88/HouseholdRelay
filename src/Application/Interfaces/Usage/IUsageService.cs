using Core.Identity;
using Core.Usage;

namespace Application.Interfaces.Usage;

/// <summary>中转请求的用量记录与查询；开始和发送前的写入失败会阻止发往上游，结束写入失败计入可见的健康状态。</summary>
public interface IUsageService
{
    /// <summary>用量时间与维护计时器共用的时钟。</summary>
    TimeProvider Clock { get; }

    /// <summary>本进程累计的用量保存失败次数。</summary>
    long PersistenceFailures { get; }

    /// <summary>当前 UTC 时间。</summary>
    DateTimeOffset Now { get; }

    /// <summary>40 天保留窗口的截止时间。</summary>
    DateTimeOffset Cutoff { get; }

    /// <summary>记录请求开始；保存失败时以 503 拒绝请求，不发往上游。</summary>
    Task StartAsync(string requestId, ClientAccess client, UsageKind kind);

    /// <summary>记录即将发送与首先要试的那一家（已保存上游主键）；保存失败时以 503 拒绝请求，不发往上游。</summary>
    Task DispatchAsync(string requestId, long version, long? upstreamCandidateId, string? model);

    /// <summary>保存请求结束结果，最多重试三次：写了返回已保存，早已结束返回无需再写，全部失败返回保存失败（已计入失败次数）。</summary>
    Task<UsageFinish> FinishAsync(string requestId, UsageEnd result);

    /// <summary>服务启动时把上次遗留的未结束记录标记为中断。</summary>
    Task RecoverAsync();

    /// <summary>分批删除超出 40 天窗口的记录，返回删除总数。</summary>
    Task<int> CleanupAsync(CancellationToken cancellation);

    /// <summary>同上，不可取消。</summary>
    Task<int> CleanupAsync() => CleanupAsync(CancellationToken.None);

    /// <summary>校验筛选条件后查询用量报告。</summary>
    Task<UsageReport> QueryAsync(UsageFilter filter);

    /// <summary>记录一次后台维护失败。</summary>
    void MaintenanceFailure();
}
