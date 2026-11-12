using Core.Configuration;

namespace Application.Interfaces.Configuration;

/// <summary>持久身份和维护幂等记录；目标编号始终只有 settings 中的一份。</summary>
public interface ISiteIdentityRepository
{
    /// <summary>读取当前设备编号、身份代次和未完成状态，损坏时明确失败。</summary>
    SiteIdentitySnapshot Read();

    /// <summary>只读查询历史操作及当前身份；未找到返回 null，不抢写锁也不生成编号。</summary>
    SiteIdentityOperationResult? Find(string operationId);

    /// <summary>在同一事务提交新编号和未完成状态；同一操作永久幂等，过期代次拒绝。</summary>
    SiteIdentityOperationResult Begin(string operationId, long expectedVersion, CancellationToken cancellation = default);

    /// <summary>派生文件与内存已对齐后完成同一操作；历史已完成操作可重复调用。</summary>
    SiteIdentityOperationResult Complete(string operationId, CancellationToken cancellation = default);
}
