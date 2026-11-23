using Application.Interfaces.Configuration;
using Core.Configuration;

namespace Tests;

/// <summary>可控的站点身份：测试能开始维护（关准入、触发思域传输停止）、换代、重新开放；许可只计数，维护操作本身不支持。</summary>
sealed class ControlledSiteIdentity(string deviceId) : ISiteIdentityService
{
    private readonly object _sync = new();
    private bool _available = true;
    private int _active;
    private CancellationTokenSource _transport = new();

    /// <summary>当前身份。</summary>
    public SiteIdentitySnapshot Current { get; private set; } = new(deviceId, 1, null, false);

    /// <summary>是否允许新的官方操作。</summary>
    // 与开始维护同锁读取。
    public bool Available { get { lock (_sync) return _available; } }

    /// <summary>固定读句。</summary>
    public string Status => "可控站点身份（测试）。";

    /// <summary>未归还的许可数。</summary>
    // 与计数增减同锁读取。
    public int Active { get { lock (_sync) return _active; } }

    /// <summary>维护中拒绝；否则计数加一，释放减一。</summary>
    public IDisposable Enter()
    {
        // 与开始维护同锁，判断与计数之间不会插进关闭。
        lock (_sync)
        {
            if (!_available) throw new SiteIdentityUnavailableException();
            _active++;
            return new Permit(this);
        }
    }

    /// <summary>这一轮准入的传输停止信号。</summary>
    // 与换代同锁读取，读到的不会是换到一半的取消源。
    public CancellationToken TransportStopping { get { lock (_sync) return _transport.Token; } }

    /// <summary>开始维护：关准入、换代传输令牌，锁外取消旧的（照正式实现）。</summary>
    public void Maintain()
    {
        CancellationTokenSource previous;
        // 关准入与换代同锁。
        lock (_sync) { _available = false; previous = _transport; _transport = new CancellationTokenSource(); }
        previous.Cancel();
    }

    /// <summary>维护完成：换成新代次并重新开放。</summary>
    public void Reopen(long version)
    {
        // 发布新身份与开放准入同锁。
        lock (_sync) { Current = Current with { Version = version }; _available = true; }
    }

    /// <summary>不支持。</summary>
    public Task PrepareAsync(CancellationToken cancellation) => throw new NotSupportedException();

    /// <summary>不支持。</summary>
    public Task<SiteIdentityOperationResult> RefreshAsync(string operationId, long expectedVersion, CancellationToken cancellation) => throw new NotSupportedException();

    /// <summary>不支持。</summary>
    public Task<SiteIdentityOperationResult> ResumeAsync(string operationId, CancellationToken cancellation) => throw new NotSupportedException();

    /// <summary>归还一次许可。</summary>
    private void Leave()
    {
        // 计数同锁。
        lock (_sync) _active--;
    }

    /// <summary>一次许可，释放即归还。</summary>
    private sealed class Permit(ControlledSiteIdentity owner) : IDisposable
    {
        private int _disposed;

        /// <summary>只归还一次。</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Leave();
        }
    }
}
