namespace Core.Upstream;

/// <summary>
/// 官方账户退役通知（按上游主键）：上游删除、导出搬走提交成功后由上游服务发布，思域会话层订阅后立即结束这个账户上的会话。
/// 只是进程内的转发点，不持有状态；用它而不是让上游服务直接依赖会话服务，是因为会话服务要按主键读上游，直接互相依赖会成环。
/// </summary>
public sealed class OfficialAccountRetirements
{
    private readonly object _sync = new();
    private Action<long>[] _observers = [];

    /// <summary>订阅退役通知；回调要快、不等网络、不得抛出，在发布方线程上同步运行。</summary>
    public void Subscribe(Action<long> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        // 订阅只在启动组装时发生，加锁保证并发订阅不丢项；发布读的是数组快照，不必持锁。
        lock (_sync) _observers = [.. _observers, observer];
    }

    /// <summary>发布一个官方账户已退役（删除或搬走，已提交）。</summary>
    public void Retire(long upstreamId)
    {
        foreach (var observer in Volatile.Read(ref _observers)) observer(upstreamId);
    }
}
