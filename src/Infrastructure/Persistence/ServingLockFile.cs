using Application.Interfaces.Usage;

namespace Infrastructure.Persistence;

/// <summary>以独占打开数据目录下 serving.lock 实现服务进程锁；第二个服务进程打开时得到 IOException。</summary>
internal sealed class ServingLockFile(RelayDatabase database) : IServingLock
{
    /// <summary>独占打开锁文件，释放返回的文件流即释放锁。</summary>
    public IDisposable Acquire() =>
        new FileStream(Path.Combine(database.State, "serving.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
}
