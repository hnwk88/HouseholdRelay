using Core.Siyu;

namespace Application.Services.Siyu;

/// <summary>
/// 内存里的一个思域会话：固定绑定、账户目录键、票据哈希，以及只在 <see cref="SiyuSessionService"/> 的锁内改动的状态、租约与名额计数。
/// <see cref="Stop"/> 在进入 Closing 时于锁外取消，隧道与发送许可都挂在它上面。
/// </summary>
internal sealed class SiyuSession(SiyuBinding binding, string accountUid, byte[] ticketHash, DateTimeOffset leaseExpiresAt)
{
    /// <summary>固定绑定。</summary>
    public SiyuBinding Binding { get; } = binding;

    /// <summary>官方账户目录键（上游 Uid），只在服务端用来取凭据，不对外。</summary>
    public string AccountUid { get; } = accountUid;

    /// <summary>票据的 SHA256；票据原文不留。</summary>
    public byte[] TicketHash { get; } = ticketHash;

    /// <summary>当前状态。</summary>
    public SiyuSessionState State { get; set; } = SiyuSessionState.Active;

    /// <summary>租约到期时刻；到点即不再准入。</summary>
    public DateTimeOffset LeaseExpiresAt { get; set; } = leaseExpiresAt;

    /// <summary>已确认的最大续租序号。</summary>
    public long LeaseSequence { get; set; }

    /// <summary>打开着的隧道数。</summary>
    public int Tunnels { get; set; }

    /// <summary>未归还的发送许可数。</summary>
    public int Permits { get; set; }

    /// <summary>结束原因；存活时为 null。</summary>
    public SiyuCloseReason? Reason { get; set; }

    /// <summary>进入 Closed 的时刻；终态按它保留。</summary>
    public DateTimeOffset? ClosedAt { get; set; }

    // 取消源只取消不释放：隧道与许可在会话关闭后仍可能读它的令牌，释放了会撞上已释放的对象；它不带计时器，不占非托管资源。
    /// <summary>会话停止信号。</summary>
    public CancellationTokenSource Stop { get; } = new();

    /// <summary>挂在站点维护停止信号上的回调登记，会话结束时注销。</summary>
    public CancellationTokenRegistration Maintenance { get; set; }
}
