using System.Collections.Frozen;

namespace Core.Siyu;

/// <summary>
/// 思域 MCP 连接器放行依据的唯一登记处：每个官方账户（按主键）一份发现集合，只存内存，服务重启即空。
/// 键是（账户主键，资料代次）；有效期 <see cref="SiyuContract.McpDiscoveryLifetime"/> 从发现开始算。
/// 只有成功的发现能写入，并且整份替换；失败什么都不做，所以不会新增许可，也不会延长过期集合。
/// 失效源：账户、资料或授权关闭时 <see cref="Invalidate"/> 整个账户；连接器被删或明确不可用时 <see cref="Forget"/> 去掉一个。
/// 失效之前就开始的发现迟到了也不再写入，避免旧结果复活已失效的许可。
/// </summary>
public sealed class McpDiscoveryRegistry(TimeProvider clock)
{
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly object _sync = new();
    private readonly Dictionary<long, McpDiscoverySet> _sets = [];
    private readonly Dictionary<long, DateTimeOffset> _invalidatedAt = [];

    /// <summary>
    /// 写入一次成功发现的结果：<paramref name="startedAt"/> 是这次发现开始的时刻（调用方在发出请求前取 <see cref="TimeProvider.GetUtcNow"/>）。
    /// 比现有集合旧、或开始于最近一次失效之前，就丢弃并返回 false。编号非空、去重后整份替换。
    /// </summary>
    public bool Replace(long accountId, string profileVersion, IEnumerable<string> connectorIds, DateTimeOffset startedAt)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(accountId);
        ArgumentException.ThrowIfNullOrEmpty(profileVersion);
        ArgumentNullException.ThrowIfNull(connectorIds);
        var ids = connectorIds.Where(id => !string.IsNullOrEmpty(id) && id.Length <= SiyuRoutes.MaxConnectorIdLength).ToFrozenSet(StringComparer.Ordinal);
        // 先后判断和替换必须在同一把锁里，否则两次并发发现或一次失效夹在中间时，旧结果会盖掉新结果或复活已失效的许可；锁内不等网络。
        lock (_sync)
        {
            if (_invalidatedAt.TryGetValue(accountId, out var invalidated) && startedAt <= invalidated) return false;
            if (_sets.TryGetValue(accountId, out var current) && startedAt < current.StartedAt) return false;
            _sets[accountId] = new McpDiscoverySet(profileVersion, ids, startedAt, startedAt + SiyuContract.McpDiscoveryLifetime);
            return true;
        }
    }

    /// <summary>按账户主键、当前资料代次查一个连接器能否放行；集合过期或代次不同都算没有有效集合。</summary>
    public McpDiscoveryCheck Check(long accountId, string profileVersion, string connectorId)
    {
        ArgumentException.ThrowIfNullOrEmpty(profileVersion);
        ArgumentException.ThrowIfNullOrEmpty(connectorId);
        var now = _clock.GetUtcNow();
        // 与替换、失效同锁，读到的一定是某一份完整集合，不会读到替换到一半的状态。
        lock (_sync)
        {
            if (!_sets.TryGetValue(accountId, out var set) || !string.Equals(set.ProfileVersion, profileVersion, StringComparison.Ordinal) || now >= set.ExpiresAt)
                return McpDiscoveryCheck.NotDiscovered;
            return set.ConnectorIds.Contains(connectorId) ? McpDiscoveryCheck.Permitted : McpDiscoveryCheck.Unknown;
        }
    }

    /// <summary>账户、资料或授权关闭：丢掉这个账户的集合，并记下失效时刻，挡住之前开始的迟到发现。</summary>
    public void Invalidate(long accountId)
    {
        var now = _clock.GetUtcNow();
        // 删集合与记失效时刻一起做，迟到的发现在同一把锁里看到这个时刻就会被挡下。
        lock (_sync)
        {
            _sets.Remove(accountId);
            _invalidatedAt[accountId] = now;
        }
    }

    /// <summary>连接器被删或明确不可用：只从这个账户的集合里去掉它，集合其余部分和有效期不变。</summary>
    public void Forget(long accountId, string connectorId)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectorId);
        // 读出旧集合再写回去掉一项的新集合，同锁防止和并发替换互相覆盖。
        lock (_sync)
        {
            if (!_sets.TryGetValue(accountId, out var set) || !set.ConnectorIds.Contains(connectorId)) return;
            _sets[accountId] = set with { ConnectorIds = set.ConnectorIds.Where(id => !string.Equals(id, connectorId, StringComparison.Ordinal)).ToFrozenSet(StringComparer.Ordinal) };
        }
    }
}
