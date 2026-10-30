using Application.Interfaces.Upstream;
using Core.Configuration;
using Core.Upstream;
using Core.Usage;

namespace Infrastructure.ExternalSources.Upstream;

/// <summary>
/// 按数据库活动版本更换在用那一家的连接，旧请求的租约继续使用原地址、凭据和状态；备用上游按已保存上游主键各持一个连接，凭据、暂停、冷却、暂避各管各的。
/// 活动版本换了，在用与备用的连接都随新版本重建。
/// </summary>
internal sealed class UpstreamPool(IActiveUpstreamSource source, RelayConfiguration config, IUpstreamProxy proxy, TimeProvider clock) : IUpstreamPool
{
    /// <summary>换掉的那一家暂避多久。</summary>
    internal static readonly TimeSpan AvoidFor = TimeSpan.FromMinutes(5);
    private readonly object sync = new();
    private readonly int maxConnections = config.MaxConcurrent;
    private UpstreamConnection? current;
    /// <summary>备用上游的连接，按已保存上游主键；<see cref="order"/> 记最近一次读到的上游页顺序。</summary>
    private readonly Dictionary<string, UpstreamConnection> standby = new(StringComparer.Ordinal);
    private List<string> order = [];
    /// <summary>每次读快照前领的序号，越大越新；<see cref="installed"/> 与 <see cref="standbyInstalled"/> 是当前在用与备用连接所用快照的序号。</summary>
    private long reads, installed, standbyInstalled;
    private string? servedId;
    private DateTimeOffset? servedAt;

    /// <summary>
    /// 取得当前活动版本的连接租约；版本不变但凭据换了（官方登录账户的令牌被 CLI 刷新）时也换新连接，新连接不带旧令牌的认证暂停。
    /// 读快照在锁外进行：官方登录令牌过期时读取可能要等刷新（最多 30 秒），不能让其他请求排在锁后面。
    /// </summary>
    public IUpstreamLease Acquire() => Active(out _);

    /// <summary>
    /// 取得这次请求的尝试顺序：先只读在用那一家，备用上游等真要用时才读。开关取这次新读的快照：
    /// 连接只在版本或凭据变化时重建，沿用的连接里记的是旧快照，开关保存后不重建连接也要立即生效。
    /// </summary>
    public IUpstreamRoute Route() { var active = Active(out var failover); return new UpstreamRoute(this, active, failover, clock); }

    /// <summary>此刻的走法：最近一次接住请求的那家，以及在用与备用里暂停、冷却、暂避中的各家（在用的排前，备用按上游页顺序）。</summary>
    public UpstreamRouting Routing()
    {
        var now = clock.GetUtcNow();
        // 读在用与备用连接要和替换它们的 Active、Standby 互斥，否则可能读到刚被释放、正被换下的连接。
        lock (sync)
        {
            var connections = new[] { current }.Concat(order.Select(id => standby.GetValueOrDefault(id)));
            return new(servedId, servedAt, connections.Select(connection => connection?.Held(now)).OfType<UpstreamAvoidance>().ToArray());
        }
    }

    /// <summary>在用那一家的连接，已增加一次引用；<paramref name="failover"/> 是这次读到的自动切换开关。</summary>
    private UpstreamConnection Active(out bool failover)
    {
        var read = Interlocked.Increment(ref reads);
        var snapshot = source.ReadActive(); failover = snapshot.Failover;
        // 比较、替换连接和增加引用必须在同一把锁里完成，否则并发请求可能拿到刚被释放的旧连接或各自建出重复连接。
        lock (sync)
        {
            // 比当前连接晚开始读的快照才可能更新；先开始、后读完的慢读不能把已换上的新连接换回去，此时沿用当前连接。
            if (current is null || read > installed && (current.Snapshot.Version != snapshot.Version || !string.Equals(current.Snapshot.Credential, snapshot.Credential, StringComparison.Ordinal)))
            {
                var replacement = new UpstreamConnection(snapshot, proxy, maxConnections);
                var previous = current; current = replacement; installed = read; previous?.Dispose();
            }
            else if (read > installed) installed = read;
            return current.Retain();
        }
    }

    /// <summary>
    /// 读备用上游并给每家换上与快照相符的连接（版本、配置或凭据变了才重建，重建后暂停、冷却、暂避清零），已不在备用列表里的连接释放；
    /// 返回已各增加一次引用的连接，按上游页顺序。读快照在锁外进行，先开始、后读完的慢读只沿用现有连接，不替换也不删除。
    /// </summary>
    internal IReadOnlyList<UpstreamConnection> Standby()
    {
        var read = Interlocked.Increment(ref reads);
        var snapshots = source.ReadStandby().Where(snapshot => snapshot.CandidateId is not null).ToArray();
        // 与在用连接同一把锁：比较、替换、删除和增加引用一起完成，并发请求不会拿到刚释放的连接或各自建出重复连接。
        lock (sync)
        {
            var leases = new List<UpstreamConnection>();
            var stale = read < standbyInstalled;
            foreach (var snapshot in snapshots)
            {
                var id = snapshot.CandidateId!;
                standby.TryGetValue(id, out var connection);
                if (!stale && (connection is null || connection.Snapshot.Version != snapshot.Version || connection.Snapshot.Options != snapshot.Options
                    || !string.Equals(connection.Snapshot.Credential, snapshot.Credential, StringComparison.Ordinal)))
                { connection?.Dispose(); connection = new UpstreamConnection(snapshot, proxy, maxConnections); standby[id] = connection; }
                if (connection is not null) leases.Add(connection.Retain());
            }
            if (stale) return leases;
            standbyInstalled = read; order = snapshots.Select(snapshot => snapshot.CandidateId!).ToList();
            foreach (var id in standby.Keys.Except(order).ToArray()) { standby[id].Dispose(); standby.Remove(id); }
            return leases;
        }
    }

    /// <summary>换掉的那一家按 <see cref="UpstreamAttempt.Avoids"/> 暂避 <see cref="AvoidFor"/>，记下最后一次失败的状态码、错误类型与连接失败分类。</summary>
    internal void Avoid(UpstreamConnection connection, UpstreamAttempt attempt)
    {
        if (!attempt.Avoids) return;
        connection.Avoid(new(connection.Snapshot.CandidateId, "avoid", clock.GetUtcNow() + AvoidFor, attempt.UpstreamStatus, attempt.ErrorType, attempt.ConnectFailure));
    }

    /// <summary>记下最近一次把成功响应交给客户端的那一家与时刻。</summary>
    internal void Served(UpstreamConnection connection)
    // 主键与时刻成对写入，页面读出时不会配错。
    { lock (sync) { servedId = connection.Snapshot.CandidateId; servedAt = clock.GetUtcNow(); } }

    /// <summary>指定版本的暂停与冷却状态。</summary>
    public (bool Paused, int CooldownSeconds) Status(long version)
    // 加锁读取，保证版本比较和状态读取针对的是同一个连接，不会在中途被 Acquire 替换。
    { lock (sync) return current?.Snapshot.Version == version ? (current.Paused, current.CooldownSeconds) : (false, 0); }

    /// <summary>释放全部连接；重复调用无副作用。</summary>
    // 与 Acquire 互斥，避免关闭时有请求正好拿到即将释放的连接；在途租约各自持有引用，不受影响。
    public void Dispose()
    {
        lock (sync)
        {
            current?.Dispose(); current = null;
            foreach (var connection in standby.Values) connection.Dispose();
            standby.Clear(); order = [];
        }
    }
}
