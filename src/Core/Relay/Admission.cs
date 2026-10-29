using System.Collections.Concurrent;
using Core.Configuration;

namespace Core.Relay;

/// <summary>按全局并发、单人并发、排队长度和每分钟次数准入中转请求。</summary>
public sealed class Admission(RelayConfiguration config)
{
    private readonly SemaphoreSlim global = new(config.MaxConcurrent);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> gates = new();
    private readonly ConcurrentDictionary<string, (long Minute, int Count)> rates = new();
    private int queued, active;
    /// <summary>当前正在排队等待名额的请求数（含刚进入、尚未拿到名额的）；无锁读取的瞬时值，仅供状态展示。</summary>
    public int Queued => Volatile.Read(ref queued);

    /// <summary>当前已拿到全局名额、正在执行的请求数；无锁读取的瞬时值，仅供状态展示。</summary>
    public int Active => Volatile.Read(ref active);

    /// <summary>按客户端名做每分钟计数（按 UTC 整分钟划窗，新分钟从 1 重新计）；超过 <see cref="RelayConfiguration.RequestsPerMinute"/> 时抛出 429 的 <see cref="RelayFailure"/>，并附上到下一分钟的剩余秒数作为重试间隔。可被多个请求线程并发调用。</summary>
    public void CheckRate(string name)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds(); var minute = now / 60;
        var item = rates.AddOrUpdate(name, (minute, 1), (_, previous) => previous.Minute == minute ? (minute, previous.Count + 1) : (minute, 1));
        if (item.Count > config.RequestsPerMinute) throw new RelayFailure(429, "Client request rate limit reached.", (int)(60 - now % 60));
    }

    /// <summary>
    /// 为客户端排队申请执行名额：先占用排队位，再依次取得本人名额和全局名额。
    /// 队列已满或排队超过 <see cref="RelayConfiguration.QueueTimeoutSeconds"/> 时抛出 429 的 <see cref="RelayFailure"/>；
    /// 调用方自身取消时原样抛出 <see cref="OperationCanceledException"/>。返回的对象释放后归还两级名额，重复释放无副作用。
    /// </summary>
    public async Task<IDisposable> Enter(string name, CancellationToken cancellation)
    {
        if (Interlocked.Increment(ref queued) > config.MaxQueued)
        { Interlocked.Decrement(ref queued); throw new RelayFailure(429, "Relay queue is full.", 5); }
        // 每个客户端一个信号量，按名称懒创建且从不移除；GetOrAdd 在并发下可能多建一个但只有一个会被存下并共用。
        var gate = gates.GetOrAdd(name, _ => new SemaphoreSlim(config.MaxPerClient));
        var gotClient = false; var gotGlobal = false;
        // 链接调用方的取消令牌，再叠加排队超时；using 保证无论成功失败都释放计时器资源。
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        // 排队等待有上限，避免请求无限挂起占用连接；超时后由下方 catch 转成 429。
        timeout.CancelAfter(TimeSpan.FromSeconds(config.QueueTimeoutSeconds));
        try
        {
            await gate.WaitAsync(timeout.Token); gotClient = true;
            await global.WaitAsync(timeout.Token); gotGlobal = true; Interlocked.Increment(ref active);
            return new Release(() => { Interlocked.Decrement(ref active); global.Release(); gate.Release(); });
        }
        // 只有排队超时才转成 429；调用方自己取消（如客户端断开）时不拦截，让取消按原异常向上传播。
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { throw new RelayFailure(429, "Relay queue wait expired.", 5); }
        // 不论成功、超时还是取消都要退出排队计数；若已拿到本人名额却没拿到全局名额，必须在此归还，否则该客户端名额会永久泄漏。
        finally
        {
            Interlocked.Decrement(ref queued);
            if (gotClient && !gotGlobal) gate.Release();
        }
    }

    /// <summary>名额归还句柄：首次释放时执行归还回调，之后的释放为空操作，防止重复归还导致信号量计数超额。</summary>
    private sealed class Release(Action release) : IDisposable
    {
        private Action? callback = release;

        /// <summary>原子地取出并清空回调后执行，保证并发或重复调用时归还动作只发生一次。</summary>
        public void Dispose() => Interlocked.Exchange(ref callback, null)?.Invoke();
    }
}
