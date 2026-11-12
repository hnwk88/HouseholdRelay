using Core.Configuration;

namespace Application.Interfaces.Configuration;

/// <summary>站点身份的运行快照、官方操作许可和管理员维护；许可必须覆盖完整响应或子进程。</summary>
public interface ISiteIdentityService
{
    /// <summary>当前唯一持久身份的运行快照，不作为网页显示值。</summary>
    SiteIdentitySnapshot Current { get; }
    /// <summary>准备已完成且此刻允许开始新的官方操作。</summary>
    bool Available { get; }
    /// <summary>管理员可读的进度和下一步，不包含设备值。</summary>
    string Status { get; }
    /// <summary>取得独立计数的官方操作许可；维护中拒绝，释放时归还计数。</summary>
    IDisposable Enter();
    /// <summary>持有服务排他锁后准备私有配置，恢复同一个待完成目标，不生成新号。</summary>
    Task Prepare(CancellationToken cancellation);
    /// <summary>关闭准入、排空、事务提交并对齐；重复操作号不会重复生成。</summary>
    Task<SiteIdentityOperationResult> Refresh(string operationId, long expectedVersion, CancellationToken cancellation);
    /// <summary>继续已持久提交的维护，不生成新目标。</summary>
    Task<SiteIdentityOperationResult> Resume(string operationId, CancellationToken cancellation);
}
