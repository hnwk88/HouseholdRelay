using Core.Usage;

namespace Application.Interfaces.Upstream;

/// <summary>
/// 一次请求的尝试顺序：在用的那一家排第一，自动切换打开时其余测试通过的上游按上游页顺序跟在后面，跳过暂停、冷却中的和模型清单里明确没有本次模型的，
/// 暂避中的排到最后；开关关闭时只有在用的那一家。每家每次请求最多给出一次。释放时归还给出过的全部租约。
/// </summary>
public interface IUpstreamRoute : IDisposable
{
    /// <summary>在用那一家的租约；开关关闭时就是唯一的一家。</summary>
    IUpstreamLease Active { get; }

    /// <summary>这次请求还有没有上游能接：开关关闭时就是在用那一家的检查；开着时在用的或任一备用能接就通过，否则抛出在用那一家的拒绝（未配置、暂停或冷却）。</summary>
    void CheckState();

    /// <summary>给出顺序里下一家还没试过的上游；没有了返回 null。第一次调用一定给出一家，一家都接不了时抛出在用那一家的拒绝。</summary>
    IUpstreamLease? Next(string? model);

    /// <summary>这家失败并被换掉：按 <see cref="UpstreamAttempt.Avoids"/> 让它暂避 5 分钟，记下原因供页面读出。</summary>
    void Avoid(IUpstreamLease lease, UpstreamAttempt attempt);

    /// <summary>这家把成功的响应交给了客户端：记为「此刻实际走的那一家」。</summary>
    void Served(IUpstreamLease lease);
}
