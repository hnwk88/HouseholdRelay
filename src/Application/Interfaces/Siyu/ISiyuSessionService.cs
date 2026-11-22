using Application.Contracts.Siyu;
using Core.Siyu;

namespace Application.Interfaces.Siyu;

/// <summary>
/// 思域会话资源：创建、查询、续租、停止，以及按官方账户、全站的收口。会话只存内存，服务重启即全部失效。
/// 所有拒绝都抛 <see cref="SiyuRefusal"/>（状态码、原因码、读句）；令牌与票据不进日志和异常消息。
/// </summary>
public interface ISiyuSessionService
{
    /// <summary>本服务进程的实例编号；重启换新，旧票据随之失效。</summary>
    string ServerInstanceId { get; }

    /// <summary>
    /// 凭个人令牌为一个官方账户（上游主键）建会话：核对协议、版本、入口、传输与身份代次，读启动资料、确认凭据可用，再发会话与票据。
    /// 同一使用者同一令牌同一 requestId 的重试在保留期内返回原结果；字段不同 409；会话已结束 409。
    /// </summary>
    Task<SiyuSessionCreated> Create(string? personalToken, SiyuCreateRequest request, CancellationToken cancellation);

    /// <summary>查询；同一使用者当前有效的令牌（包括轮换后的新版）都可查自己的旧会话终态。不续租、不读官方令牌。</summary>
    Task<SiyuSessionView> Read(string? personalToken, string sessionId);

    /// <summary>续租：只认创建它的那一版令牌、只对存活会话；序号更大才延长，同序号或更小只回现值。顺带核对资料代次，变了就结束会话。</summary>
    Task<SiyuSessionView> Renew(string? personalToken, string sessionId, long leaseSequence);

    /// <summary>停止：同一使用者当前有效的令牌都可结束自己的会话；先禁新准入、取消在途，返回 Closed（已归还）或 Closing（还在收尾）。重复停止幂等。</summary>
    Task<SiyuSessionView> Close(string? personalToken, string sessionId);

    /// <summary>结束一个官方账户（上游主键）上的全部会话，并作废它的 MCP 发现集合；账户删除、搬走、停用、换登录时调用。</summary>
    void CloseAccount(long officialAccountId, SiyuCloseReason reason);
}
