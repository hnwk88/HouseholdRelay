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

    /// <summary>只核对个人令牌（控制面在读请求体之前调用，无效 401、名单读不到 503）；不碰会话。</summary>
    Task AuthorizeAsync(string? personalToken);

    /// <summary>凭个人令牌列出能用于思域的官方账户（主键与显示名称），判据与建会话绑定账户同一处定义；一个都没有回空表。按上游创建先后排列。</summary>
    Task<IReadOnlyList<SiyuAccountOption>> ListAccountsAsync(string? personalToken);

    /// <summary>
    /// 凭个人令牌建会话：版本 1 绑请求里的官方账户（上游主键），版本 2 由后端按在用上游选官方模式（结果是 <see cref="SiyuOfficialSessionCreated"/>）或标准模式（<see cref="SiyuStandardSessionCreated"/>）。
    /// 官方模式核对协议、版本、入口、传输与身份代次，读启动资料、确认凭据可用，再发会话与票据。
    /// 同一使用者同一令牌同一 requestId 的重试在保留期内返回原结果；字段不同 409；会话已结束 409。版本 2 可自动处理的拒绝带 <see cref="SiyuRefusal.Remedy"/>。
    /// </summary>
    Task<SiyuSessionCreated> CreateAsync(string? personalToken, SiyuCreateRequest request, CancellationToken cancellation);

    /// <summary>查询；同一使用者当前有效的令牌（包括轮换后的新版）都可查自己的旧会话终态。不续租、不读官方令牌。</summary>
    Task<SiyuSessionView> ReadAsync(string? personalToken, string sessionId);

    /// <summary>
    /// 续租：只认创建它的那一版令牌、只对存活会话；序号更大才延长，同序号或更小只回现值。顺带核对资料代次，变了就结束会话；
    /// 版本 2 会话还核对后端此刻的选择与身份不符标记。<paramref name="clientProtocolVersion"/> 是请求声明的版本，只在会话找不到时用来决定拒绝带不带打回。
    /// </summary>
    Task<SiyuSessionView> RenewAsync(string? personalToken, string sessionId, long leaseSequence, int clientProtocolVersion = SiyuContract.LegacyProtocolVersion);

    /// <summary>停止：同一使用者当前有效的令牌都可结束自己的会话；先禁新准入、取消在途，返回 Closed（已归还）或 Closing（还在收尾）。重复停止幂等。</summary>
    Task<SiyuSessionView> CloseAsync(string? personalToken, string sessionId);

    /// <summary>结束一个官方账户（上游主键）上的全部会话，并作废它的 MCP 发现集合；账户删除、搬走、停用、换登录时调用。</summary>
    void CloseAccount(long officialAccountId, SiyuCloseReason reason);

    /// <summary>管理页用：全部还在内存里的会话（存活、收尾中、保留期内已结束的），按建立先后排列。</summary>
    IReadOnlyList<SiyuSessionSummary> List();

    /// <summary>管理员结束一个会话（本机管理员会话已鉴权，不需要个人令牌）；找不到或已结束什么也不做。</summary>
    void CloseByAdministrator(string sessionId);
}
