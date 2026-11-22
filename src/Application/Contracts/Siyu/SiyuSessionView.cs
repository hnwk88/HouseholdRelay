using Core.Siyu;

namespace Application.Contracts.Siyu;

/// <summary>
/// 思域会话的状态视图（查询、续租、停止都回它）：绑定、状态、租约到期与建议续租秒数；结束了带原因码与读句。不含票据与任何令牌。
/// </summary>
public sealed record SiyuSessionView(SiyuBinding Binding, SiyuSessionState State, DateTimeOffset LeaseExpiresAt, int RenewAfterSeconds,
    string? CloseCode = null, string? CloseSentence = null);
