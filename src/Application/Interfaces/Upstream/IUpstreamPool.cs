using Core.Upstream;

namespace Application.Interfaces.Upstream;

/// <summary>按活动版本更换上游连接；旧请求的租约继续使用原地址、凭据和状态。</summary>
public interface IUpstreamPool : IDisposable
{
    /// <summary>取得当前活动版本的连接租约，版本变化时换用新连接。</summary>
    IUpstreamLease Acquire();

    /// <summary>取得这次请求的尝试顺序；按候选主键各持一个连接，凭据、暂停、冷却、暂避各管各的。</summary>
    IUpstreamRoute Route();

    /// <summary>此刻的走法：最近一次接住请求的那家，以及暂避、冷却、暂停中的各家，供页面读出。</summary>
    UpstreamRouting Routing();

    /// <summary>指定版本的运行状态；该版本不是当前连接时视为正常。</summary>
    (bool Paused, int CooldownSeconds) Status(long version);
}
