namespace Application.Interfaces.Siyu;

/// <summary>
/// 一次内层请求的发送许可：从准入一直持有到回应体转发完毕。释放即归还。
/// 官方令牌只经 <see cref="AccessToken"/> 交给传输层写进这一次请求头，不进日志、不进异常消息。
/// </summary>
public interface ISiyuPermit : IAsyncDisposable
{
    /// <summary>这一次发送要用的官方访问令牌。</summary>
    string AccessToken { get; }

    /// <summary>会话关闭或这次许可被撤回时取消；传输层用它读请求体、发送和转发回应。</summary>
    CancellationToken Cancellation { get; }

    /// <summary>请求体读完、真正写给官方之前再核对一次（排队期间会话可能已关闭）；不再准入抛 <see cref="Core.Siyu.SiyuRefusal"/>。</summary>
    void Recheck();

    /// <summary>官方回了最终状态码（或本地因 3xx 拒绝转发）时报告一次，错误归属与用量由会话层决定。</summary>
    void Observe(int status);

    /// <summary>
    /// 连接器发现（<c>GET /v1/mcp_servers</c>）官方回了 200 且读出了编号：在把回应转给客户端之前报告，会话层按这次准入时刻整份替换该账户的发现集合，
    /// 并记下 <paramref name="requestTemplate"/>（客户端真实发出的请求头，个人令牌已抹掉）供以后单飞补发现原样重发。
    /// </summary>
    void ReportDiscovery(byte[] requestTemplate, IReadOnlyList<string> connectorIds);

    /// <summary>这次请求的用量累加器：推理、估算、模型目录三种用途才有（与旧接法同一套用量记录），其余用途为 null。传输层把回应体旁路交给它。</summary>
    Core.Usage.UsageAccumulator? Usage { get; }

    /// <summary>
    /// 真正写给官方之前调用一次：先只读核对 Messages 正文的设备段与账户段（不一致抛 <see cref="Core.Siyu.SiyuRefusal"/> 403，不发），再把用量记录标记为已发出（模型名取请求体顶层的 model）。记不下就抛 503，这次请求不发
    /// （与旧接法同一规则：不产生无法计量的官方调用）。没有用量记录的用途什么也不做。
    /// </summary>
    Task DispatchingAsync(ReadOnlyMemory<byte> body, CancellationToken cancellation);

    /// <summary>回应转发结束时报告一次：客户端实际收到的状态码，以及回应体是否完整转发。释放许可时据此写用量结束记录。</summary>
    void Completed(int clientStatus, bool complete);
}
