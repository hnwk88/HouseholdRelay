using Application.Interfaces.Upstream;
using Core.Usage;

namespace Infrastructure.ExternalSources.Upstream;

/// <summary>
/// 一次请求的尝试顺序。开关关闭时只有在用的那一家，与没有自动切换时逐字相同；开着时在用的排第一，备用上游按上游页顺序跟在后面，
/// 跳过暂停、冷却中的和模型清单里明确没有本次模型的（在用那家不按清单跳过），暂避中的排到最后。备用上游第一次用到时才读，成功路径不多读库。
/// </summary>
internal sealed class UpstreamRoute(UpstreamPool pool, UpstreamConnection active, bool failover, TimeProvider clock) : IUpstreamRoute
{
    /// <summary>本次请求持有引用的全部连接，释放时逐个归还。</summary>
    private readonly List<UpstreamConnection> held = [active];
    private readonly HashSet<UpstreamConnection> tried = [];
    private IReadOnlyList<UpstreamConnection>? standby;

    /// <summary>在用那一家的租约。</summary>
    public IUpstreamLease Active => active;

    /// <summary>开关关闭、或在用那家能接时就是它自己的检查；否则只要有一家备用能接就通过，都不能接时抛出在用那一家的拒绝。</summary>
    public void CheckState()
    {
        if (!failover || active.Usable || !Standby().Any(connection => connection.Usable)) active.CheckState();
    }

    /// <summary>给出顺序里下一家还没试过的上游；第一次调用一定给出一家，一家都接不了时抛出在用那一家的拒绝。</summary>
    public IUpstreamLease? Next(string? model)
    {
        var now = clock.GetUtcNow(); UpstreamConnection? next;
        if (!failover) next = tried.Count == 0 ? active : null;
        // 在用那家能接又没在暂避：直接用它，不读备用上游，成功路径与关闭开关时一样只读一次库。
        else if (tried.Count == 0 && active.Usable && !active.Avoided(now)) next = active;
        else
        {
            var candidates = Standby().Prepend(active).Where(connection => !tried.Contains(connection) && connection.Usable && (connection == active || Offers(connection, model))).ToArray();
            next = candidates.FirstOrDefault(connection => !connection.Avoided(now)) ?? candidates.FirstOrDefault();
        }
        // 第一家都给不出：在用那家要么本来就不能接（抛出它的拒绝），要么能接却被模型清单之类挡掉了备用，仍交给它。
        if (next is null && tried.Count == 0) { active.CheckState(); next = active; }
        if (next is not null) tried.Add(next);
        return next;
    }

    /// <summary>这家失败并被换掉：交给连接池按失败类型决定是否暂避 5 分钟。</summary>
    public void Avoid(IUpstreamLease lease, UpstreamAttempt attempt) => pool.Avoid((UpstreamConnection)lease, attempt);

    /// <summary>这家把成功的响应交给了客户端。</summary>
    public void Served(IUpstreamLease lease) => pool.Served((UpstreamConnection)lease);

    /// <summary>归还本次请求持有的全部连接引用。</summary>
    public void Dispose() { foreach (var connection in held) connection.Dispose(); held.Clear(); }

    /// <summary>备用上游连接：第一次用到时向连接池读，去掉与在用那家同一主键的（读的间隙里刚好切换了在用上游时会出现）。</summary>
    private IReadOnlyList<UpstreamConnection> Standby()
    {
        if (standby is not null) return standby;
        var leases = pool.Standby(); held.AddRange(leases);
        return standby = leases.Where(connection => connection.Snapshot.CandidateId != active.Snapshot.CandidateId).ToArray();
    }

    /// <summary>备用上游的模型清单里有没有本次模型：请求没带模型、清单未知或为空时照试。</summary>
    private static bool Offers(UpstreamConnection connection, string? model) =>
        model is null || connection.Snapshot.Report?.Models is not { Length: > 0 } models || models.Contains(model, StringComparer.Ordinal);
}
