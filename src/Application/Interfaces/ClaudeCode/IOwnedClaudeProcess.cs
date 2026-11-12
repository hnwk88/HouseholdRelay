namespace Application.Interfaces.ClaudeCode;

/// <summary>一次自有子进程的持久生命周期；Dispose 不清理尚未确证退出的登记。</summary>
public interface IOwnedClaudeProcess : IDisposable
{
    /// <summary>启动成功后原子记录进程号和 UTC 启动时刻，用于辨别进程号重用。</summary>
    void Register(int processId, DateTimeOffset startedAt);

    /// <summary>仅在操作系统明确报告未成功启动时取消 starting；禁止用于崩溃恢复或未知子进程状态。</summary>
    void CancelBeforeStart();

    /// <summary>调用方等待进程真正退出后清除登记；实现仍核对存活状态，未知 starting 不可清除。</summary>
    void Complete();
}
