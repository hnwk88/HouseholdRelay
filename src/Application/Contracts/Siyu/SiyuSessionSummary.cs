using Core.Siyu;

namespace Application.Contracts.Siyu;

/// <summary>管理页看的一个思域会话：绑定（全部主键与代次）、状态、租约到期、开着的隧道与在途请求数，结束了带原因。不含票据与任何令牌。</summary>
public sealed record SiyuSessionSummary(SiyuBinding Binding, SiyuSessionState State, DateTimeOffset LeaseExpiresAt, int Tunnels, int Permits, SiyuCloseReason? CloseReason);
