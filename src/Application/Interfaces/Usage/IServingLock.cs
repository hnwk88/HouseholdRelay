namespace Application.Interfaces.Usage;

/// <summary>同一数据目录只允许一个服务进程做启动恢复与定时清理的独占锁。</summary>
public interface IServingLock
{
    /// <summary>取得独占锁；已被另一服务进程持有时抛出 IOException。释放返回值即释放锁。</summary>
    IDisposable Acquire();
}
