using Application.Interfaces.Siyu;
using Core.Siyu;

namespace Application.Services.Siyu;

/// <summary>一条已认证的隧道：固定属于一个会话；准入交给会话服务；释放只归还一次名额。</summary>
internal sealed class SiyuTunnel(SiyuSessionService owner, SiyuSession session) : ISiyuTunnel
{
    private int _disposed;

    /// <summary>会话停止信号。</summary>
    public CancellationToken Closed => session.Stop.Token;

    /// <summary>一次内层请求的准入。</summary>
    public Task<ISiyuPermit> Admit(SiyuRoute route, string bearerToken, CancellationToken cancellation) => owner.Admit(session, route, bearerToken, cancellation);

    /// <summary>归还隧道名额；重复释放不重复归还。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.ReleaseTunnel(session);
    }
}
