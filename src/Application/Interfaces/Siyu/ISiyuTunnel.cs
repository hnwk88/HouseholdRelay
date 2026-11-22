using Core.Siyu;

namespace Application.Interfaces.Siyu;

/// <summary>一条已认证的思域隧道（一条外层连接），固定属于一个会话和一个官方主机。释放即归还隧道名额。</summary>
public interface ISiyuTunnel : IDisposable
{
    /// <summary>会话关闭（停止、撤销、维护、租约到期、服务退出）时取消；传输层据此立即关闭内外两层连接。</summary>
    CancellationToken Closed { get; }

    /// <summary>
    /// 一个内层请求的准入：核对客户端出示的个人令牌属于这个会话的令牌代次、会话仍可用、路由在许可内（连接器编号在当次发现集合里），
    /// 再取一份有效官方凭据，返回发送许可。不准入抛 <see cref="SiyuRefusal"/>。<paramref name="cancellation"/> 只取消本调用者的等待。
    /// </summary>
    Task<ISiyuPermit> Admit(SiyuRoute route, string bearerToken, CancellationToken cancellation);
}
