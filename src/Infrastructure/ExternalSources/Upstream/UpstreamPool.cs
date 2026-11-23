using Application.Interfaces.Upstream;
using Application.Interfaces.Configuration;
using Core.Configuration;
using Core.Upstream;
using Core.Usage;

namespace Infrastructure.ExternalSources.Upstream;

/// <summary>
/// 按数据库活动版本更换在用那一家的连接，旧请求的租约继续使用原地址、凭据和状态；备用上游按已保存上游主键各持一个连接，凭据、暂停、冷却、暂避各管各的。
/// 活动版本换了，在用与备用的连接都随新版本重建。
/// </summary>
internal sealed class UpstreamPool : IUpstreamPool
{
    private readonly IActiveUpstreamSource _source;
    private readonly IUpstreamProxy _proxy;
    private readonly TimeProvider _clock;
    /// <summary>此刻能否发往官方账户：正式服务问站点身份（维护中为假），独立中转没有站点身份，恒为真。</summary>
    private readonly Func<bool> _officialAvailable;

    /// <summary>正式服务：五个依赖全部必填，构造时当场判空；官方账户能否发由站点身份决定。</summary>
    public UpstreamPool(IActiveUpstreamSource source, RelayConfiguration config, IUpstreamProxy proxy, TimeProvider clock, ISiteIdentityService siteIdentity)
        : this(source, config, proxy, clock, Available(siteIdentity)) { }

    /// <summary>独立中转（不连数据库、没有站点身份）：官方账户恒可发，与站点身份无关。</summary>
    public static UpstreamPool Standalone(IActiveUpstreamSource source, RelayConfiguration config, IUpstreamProxy proxy, TimeProvider clock) =>
        new(source, config, proxy, clock, () => true);

    /// <summary>两个入口的汇合处。</summary>
    private UpstreamPool(IActiveUpstreamSource source, RelayConfiguration config, IUpstreamProxy proxy, TimeProvider clock, Func<bool> officialAvailable)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(config); ArgumentNullException.ThrowIfNull(proxy); ArgumentNullException.ThrowIfNull(clock);
        _source = source; _proxy = proxy; _clock = clock; _officialAvailable = officialAvailable; _maxConnections = config.MaxConcurrent;
    }

    /// <summary>站点身份判空后读成「此刻能否发往官方」。</summary>
    private static Func<bool> Available(ISiteIdentityService siteIdentity)
    {
        ArgumentNullException.ThrowIfNull(siteIdentity);
        return () => siteIdentity.Available;
    }
    /// <summary>换掉的那一家暂避多久。</summary>
    internal static readonly TimeSpan AvoidFor = TimeSpan.FromMinutes(5);
    private readonly object _sync = new();
    private readonly int _maxConnections;
    private UpstreamConnection? _current;
    /// <summary>备用上游的连接，按已保存上游主键；<see cref="_order"/> 记最近一次读到的上游页顺序。</summary>
    private readonly Dictionary<long, UpstreamConnection> _standby = [];
    private List<long> _order = [];
    /// <summary>每次读快照前领的序号，越大越新；<see cref="_installed"/> 与 <see cref="_standbyInstalled"/> 是当前在用与备用连接所用快照的序号。</summary>
    private long _reads, _installed, _standbyInstalled;
    private long? _servedId;
    private DateTimeOffset? _servedAt;

    /// <summary>
    /// 取得当前活动版本的连接租约；版本不变但凭据换了（官方登录账户的令牌被 CLI 刷新）时也换新连接，新连接不带旧令牌的认证暂停。
    /// 读快照在锁外进行：官方登录令牌过期时读取可能要等刷新（最多 30 秒），不能让其他请求排在锁后面。
    /// </summary>
    public async Task<IUpstreamLease> AcquireAsync() => (await ActiveAsync()).Connection;

    /// <summary>
    /// 取得这次请求的尝试顺序：先只读在用那一家，备用上游等真要用时才读。开关取这次新读的快照：
    /// 连接只在版本或凭据变化时重建，沿用的连接里记的是旧快照，开关保存后不重建连接也要立即生效。
    /// </summary>
    public async Task<IUpstreamRoute> RouteAsync() { var (active, failover) = await ActiveAsync(); return new UpstreamRoute(this, active, failover, _clock); }

    /// <summary>此刻的走法：最近一次接住请求的那家，以及在用与备用里暂停、冷却、暂避中的各家（在用的排前，备用按上游页顺序）。</summary>
    public UpstreamRouting Routing()
    {
        var now = _clock.GetUtcNow();
        // 读在用与备用连接要和替换它们的 Active、Standby 互斥，否则可能读到刚被释放、正被换下的连接。
        lock (_sync)
        {
            var connections = new[] { _current }.Concat(_order.Select(id => _standby.GetValueOrDefault(id)));
            return new(_servedId, _servedAt, connections.Select(connection => connection?.Held(now)).OfType<UpstreamAvoidance>().ToArray());
        }
    }

    /// <summary>在用那一家的连接（已增加一次引用）与这次读到的自动切换开关。</summary>
    private async Task<(UpstreamConnection Connection, bool Failover)> ActiveAsync()
    {
        var read = Interlocked.Increment(ref _reads);
        var snapshot = await _source.ReadActiveAsync(); var failover = snapshot.Failover;
        // 比较、替换连接和增加引用必须在同一把锁里完成，否则并发请求可能拿到刚被释放的旧连接或各自建出重复连接。
        lock (_sync)
        {
            // 比当前连接晚开始读的快照才可能更新；先开始、后读完的慢读不能把已换上的新连接换回去，此时沿用当前连接。
            if (_current is not null && snapshot.BlockedByIdentity && _current.Snapshot.ActiveVersion == snapshot.ActiveVersion
                && _current.Snapshot.UpstreamCandidateId == snapshot.UpstreamCandidateId && _current.Snapshot.Options == snapshot.Options)
            {
                if (read > _installed) { _installed = read; _current.SetIdentityReadBlocked(true, snapshot.BlockedReason); }
                return (_current.Retain(), failover);
            }
            // 账户编号和凭据一样随账户目录变化（重新登录换了账户）；不比它会让在用连接继续带旧账户段。
            if (_current is null || read > _installed && (_current.Snapshot.ActiveVersion != snapshot.ActiveVersion || !string.Equals(_current.Snapshot.Credential, snapshot.Credential, StringComparison.Ordinal)
                || !string.Equals(_current.Snapshot.AccountUuid, snapshot.AccountUuid, StringComparison.Ordinal)))
            {
                var replacement = new UpstreamConnection(snapshot, _proxy, _maxConnections, _officialAvailable);
                var previous = _current; _current = replacement; _installed = read; previous?.Dispose();
            }
            else if (read > _installed) { _installed = read; _current.SetIdentityReadBlocked(snapshot.BlockedByIdentity, snapshot.BlockedReason); }
            return (_current.Retain(), failover);
        }
    }

    /// <summary>
    /// 读备用上游并给每家换上与快照相符的连接（版本、配置或凭据变了才重建，重建后暂停、冷却、暂避清零），已不在备用列表里的连接释放；
    /// 返回已各增加一次引用的连接，按上游页顺序。读快照在锁外进行，先开始、后读完的慢读只沿用现有连接，不替换也不删除。
    /// </summary>
    internal async Task<IReadOnlyList<UpstreamConnection>> StandbyAsync()
    {
        var read = Interlocked.Increment(ref _reads);
        var snapshots = (await _source.ReadStandbyAsync()).Where(snapshot => snapshot.UpstreamCandidateId is not null).ToArray();
        // 与在用连接同一把锁：比较、替换、删除和增加引用一起完成，并发请求不会拿到刚释放的连接或各自建出重复连接。
        lock (_sync)
        {
            var leases = new List<UpstreamConnection>();
            var stale = read < _standbyInstalled;
            foreach (var snapshot in snapshots)
            {
                var id = snapshot.UpstreamCandidateId!.Value;
                _standby.TryGetValue(id, out var connection);
                var keepBlocked = snapshot.BlockedByIdentity && connection is not null && connection.Snapshot.ActiveVersion == snapshot.ActiveVersion && connection.Snapshot.Options == snapshot.Options;
                if (!stale && !keepBlocked && (connection is null || connection.Snapshot.ActiveVersion != snapshot.ActiveVersion || connection.Snapshot.Options != snapshot.Options
                    || !string.Equals(connection.Snapshot.Credential, snapshot.Credential, StringComparison.Ordinal)
                    || !string.Equals(connection.Snapshot.AccountUuid, snapshot.AccountUuid, StringComparison.Ordinal)))
                { connection?.Dispose(); connection = new UpstreamConnection(snapshot, _proxy, _maxConnections, _officialAvailable); _standby[id] = connection; }
                if (connection is not null)
                {
                    if (!stale) connection.SetIdentityReadBlocked(snapshot.BlockedByIdentity, snapshot.BlockedReason);
                    leases.Add(connection.Retain());
                }
            }
            if (stale) return leases;
            _standbyInstalled = read; _order = snapshots.Select(snapshot => snapshot.UpstreamCandidateId!.Value).ToList();
            foreach (var id in _standby.Keys.Except(_order).ToArray()) { _standby[id].Dispose(); _standby.Remove(id); }
            return leases;
        }
    }

    /// <summary>换掉的那一家按 <see cref="UpstreamAttempt.Avoids"/> 暂避 <see cref="AvoidFor"/>，记下最后一次失败的状态码、错误类型与连接失败分类。</summary>
    internal void Avoid(UpstreamConnection connection, UpstreamAttempt attempt)
    {
        if (!attempt.Avoids) return;
        connection.Avoid(new(connection.Snapshot.UpstreamCandidateId, AvoidanceKind.Avoid, _clock.GetUtcNow() + AvoidFor, attempt.UpstreamStatus, attempt.ErrorType, attempt.ConnectFailure));
    }

    /// <summary>记下最近一次把成功响应交给客户端的那一家与时刻。</summary>
    internal void Served(UpstreamConnection connection)
    // 主键与时刻成对写入，页面读出时不会配错。
    { lock (_sync) { _servedId = connection.Snapshot.UpstreamCandidateId; _servedAt = _clock.GetUtcNow(); } }

    /// <summary>指定版本的暂停与冷却状态。</summary>
    public (bool Paused, int CooldownSeconds) Status(long version)
    // 加锁读取，保证版本比较和状态读取针对的是同一个连接，不会在中途被 Acquire 替换。
    { lock (_sync) return _current?.Snapshot.ActiveVersion == version ? (_current.Paused, _current.CooldownSeconds) : (false, 0); }

    /// <summary>释放全部连接；重复调用无副作用。</summary>
    // 与 Acquire 互斥，避免关闭时有请求正好拿到即将释放的连接；在途租约各自持有引用，不受影响。
    public void Dispose()
    {
        lock (_sync)
        {
            _current?.Dispose(); _current = null;
            foreach (var connection in _standby.Values) connection.Dispose();
            _standby.Clear(); _order = [];
        }
    }
}
