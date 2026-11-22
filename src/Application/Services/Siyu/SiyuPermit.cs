using Application.Interfaces.Siyu;
using Core.Siyu;

namespace Application.Services.Siyu;

/// <summary>
/// 一次发送许可：持有站点官方操作许可（维护排空会等它）、这次的官方凭据与取消源（会话停止即取消）。释放时依次归还，只归还一次。
/// </summary>
internal sealed class SiyuPermit(SiyuSessionService owner, SiyuSession session, IDisposable operation, OfficialCredentialLease credential, SiyuRoute route, DateTimeOffset admittedAt,
    CancellationTokenSource cancel) : ISiyuPermit
{
    private int _disposed;

    /// <summary>这次发送用的官方访问令牌。</summary>
    public string AccessToken => credential.AccessToken;

    /// <summary>会话停止即取消。</summary>
    public CancellationToken Cancellation => cancel.Token;

    /// <summary>真正写给官方之前再核对一次会话。</summary>
    public void Recheck() => owner.Recheck(session);

    /// <summary>报告官方最终状态码，归属按这次凭据代次。</summary>
    public void Observe(int status) => owner.Observe(session, route, credential, status);

    /// <summary>报告一次成功的连接器发现，按准入时刻替换发现集合（迟于失效或更新发现的旧结果不生效）。</summary>
    public void ReportDiscovery(byte[] requestTemplate, IReadOnlyList<string> connectorIds) => owner.Discovered(session, route, admittedAt, requestTemplate, connectorIds);

    /// <summary>归还站点许可、取消源与会话名额；重复释放只做一次。</summary>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        operation.Dispose();
        cancel.Dispose();
        owner.ReleasePermit(session);
        return ValueTask.CompletedTask;
    }
}
