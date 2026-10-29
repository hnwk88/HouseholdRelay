using Application.Interfaces.Upstream;
using Core.Configuration;

namespace Infrastructure.ExternalSources.Upstream;

/// <summary>按数据库活动版本更换连接，旧请求的租约继续使用原地址、凭据和状态。</summary>
internal sealed class UpstreamPool(IActiveUpstreamSource source, RelayConfiguration config, IUpstreamProxy proxy) : IUpstreamPool
{
    private readonly object sync = new();
    private readonly int maxConnections = config.MaxConcurrent;
    private UpstreamConnection? current;
    /// <summary>每次读快照前领的序号，越大越新；<see cref="installed"/> 是当前连接所用快照的序号。</summary>
    private long reads, installed;

    /// <summary>
    /// 取得当前活动版本的连接租约；版本不变但凭据换了（官方登录账户的令牌被 CLI 刷新）时也换新连接，新连接不带旧令牌的认证暂停。
    /// 读快照在锁外进行：官方登录令牌过期时读取可能要等刷新（最多 30 秒），不能让其他请求排在锁后面。
    /// </summary>
    public IUpstreamLease Acquire()
    {
        var order = Interlocked.Increment(ref reads);
        var snapshot = source.ReadActive();
        // 比较、替换连接和增加引用必须在同一把锁里完成，否则并发请求可能拿到刚被释放的旧连接或各自建出重复连接。
        lock (sync)
        {
            // 比当前连接晚开始读的快照才可能更新；先开始、后读完的慢读不能把已换上的新连接换回去，此时沿用当前连接。
            if (current is null || order > installed && (current.Snapshot.Version != snapshot.Version || !string.Equals(current.Snapshot.Credential, snapshot.Credential, StringComparison.Ordinal)))
            {
                var replacement = new UpstreamConnection(snapshot, proxy, maxConnections);
                var previous = current; current = replacement; installed = order; previous?.Dispose();
            }
            else if (order > installed) installed = order;
            return current.Retain();
        }
    }

    /// <summary>指定版本的暂停与冷却状态。</summary>
    public (bool Paused, int CooldownSeconds) Status(long version)
    // 加锁读取，保证版本比较和状态读取针对的是同一个连接，不会在中途被 Acquire 替换。
    { lock (sync) return current?.Snapshot.Version == version ? (current.Paused, current.CooldownSeconds) : (false, 0); }

    /// <summary>释放当前连接；重复调用无副作用。</summary>
    // 与 Acquire 互斥，避免关闭时有请求正好拿到即将释放的连接；在途租约各自持有引用，不受影响。
    public void Dispose() { lock (sync) { current?.Dispose(); current = null; } }
}
