using Application.Interfaces.Siyu;
using Core.Siyu;
using Core.Usage;

namespace Application.Services.Siyu;

/// <summary>
/// 一次发送许可：持有站点官方操作许可（维护排空会等它）、这次的官方凭据与取消源（会话停止即取消），以及这次请求的用量记录状态。
/// 释放时先写用量结束记录，再依次归还站点许可、取消源与会话名额，只做一次。
/// </summary>
internal sealed class SiyuPermit(SiyuSessionService owner, SiyuSession session, IDisposable operation, OfficialCredentialLease credential, SiyuRoute route, DateTimeOffset admittedAt,
    CancellationTokenSource cancel, string? usageRequestId, string userName) : ISiyuPermit
{
    private int _disposed;
    private bool _dispatched;
    private int? _upstreamStatus;
    private int _clientStatus = 502;
    private bool _complete;

    /// <summary>这次发送用的官方访问令牌。</summary>
    public string AccessToken => credential.AccessToken;

    /// <summary>会话停止即取消。</summary>
    public CancellationToken Cancellation => cancel.Token;

    /// <summary>有用量记录的用途才有累加器。</summary>
    public UsageAccumulator? Usage { get; } = usageRequestId is null ? null : new UsageAccumulator(SiyuSessionService.UsageKindOf(route.Purpose)!.Value);

    /// <summary>真正写给官方之前再核对一次会话。</summary>
    public void Recheck() => owner.Recheck(session);

    /// <summary>报告官方最终状态码，归属按这次凭据代次。</summary>
    public void Observe(int status)
    {
        _upstreamStatus = status;
        owner.Observe(session, route, credential, status);
    }

    /// <summary>报告一次成功的连接器发现，按准入时刻替换发现集合（迟于失效或更新发现的旧结果不生效）。</summary>
    public void ReportDiscovery(byte[] requestTemplate, IReadOnlyList<string> connectorIds) => owner.Discovered(session, route, admittedAt, requestTemplate, connectorIds);

    /// <summary>写给官方之前：先核对 Messages 正文的身份两段，再把用量记录标记为已发出（只做一次）。</summary>
    public async Task Dispatching(ReadOnlyMemory<byte> body, CancellationToken cancellation)
    {
        // 先只读核对身份（Messages 的设备段与账户段），不一致就本地拒绝，用量也不记已发出。
        owner.VerifyBody(session, route, body.Span, userName);
        if (usageRequestId is null || _dispatched) return;
        await owner.DispatchUsage(session, usageRequestId, body);
        _dispatched = true;
    }

    /// <summary>记下客户端收到的状态码与回应体是否完整。</summary>
    public void Completed(int clientStatus, bool complete) { _clientStatus = clientStatus; _complete = complete; }

    /// <summary>先写用量结束记录（失败只计数，不影响归还），再归还站点许可、取消源与会话名额；重复释放只做一次。</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            if (usageRequestId is not null)
                await owner.FinishUsage(usageRequestId, Usage!, _dispatched, _upstreamStatus, _clientStatus, _complete, cancel.IsCancellationRequested, admittedAt);
        }
        // 用量写完（或写失败）之后才归还，维护排空不会在用量落账前提交。
        finally
        {
            operation.Dispose();
            cancel.Dispose();
            owner.ReleasePermit(session);
        }
    }
}
