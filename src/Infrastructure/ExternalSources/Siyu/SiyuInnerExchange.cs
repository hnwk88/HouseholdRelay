using Application.Interfaces.Siyu;
using Core.Siyu;

namespace Infrastructure.ExternalSources.Siyu;

/// <summary>
/// 一条内层 TLS 连接上的逐请求处理，严格按顺序（流水线里的下一个请求要等上一个回应写完才解析）：
/// 读头并按合同定界 → Host 与隧道主机一致 → 路由白名单 → 取唯一个人令牌 → 会话准入拿发送许可 → 读请求体 → 再核对 →
/// 原始头字节只换 Authorization、请求体原样，写给官方 → 回应头原样转发（3xx 换成本地 502，不带 Location）→ 回应体按定界边到边转 → 报告状态 → 归还许可。
/// 出站连接跟着这条内层连接复用；上游要求关闭或读到关闭为止时重新建连。
/// </summary>
public sealed class SiyuInnerExchange
{
    private readonly Stream _inner;
    private readonly OfficialHost _host;
    private readonly ISiyuTunnel _tunnel;
    private readonly IOfficialConnector _connector;
    private readonly SiyuProxyLimits _limits;
    /// <summary>补发现模板里代替个人令牌的占位值：模板只留在内存，重发时整段换成官方令牌，个人令牌不随模板留存。</summary>
    internal const string TemplatePlaceholder = "siyu-template";

    private Stream? _upstream;
    private Http1Reader? _upstreamReader;

    /// <summary>全部依赖必填，构造时当场判空。</summary>
    public SiyuInnerExchange(Stream inner, OfficialHost host, ISiyuTunnel tunnel, IOfficialConnector connector, SiyuProxyLimits limits)
    {
        ArgumentNullException.ThrowIfNull(inner); ArgumentNullException.ThrowIfNull(tunnel); ArgumentNullException.ThrowIfNull(connector); ArgumentNullException.ThrowIfNull(limits);
        _inner = inner; _host = host; _tunnel = tunnel; _connector = connector; _limits = limits;
    }

    /// <summary>处理到客户端关闭、空闲超时、会话关闭或任一请求要求关闭为止；退出时关闭出站连接。</summary>
    public async Task Run(CancellationToken closed)
    {
        var reader = new Http1Reader(_inner);
        try
        {
            while (!closed.IsCancellationRequested)
            {
                byte[]? raw;
                // 两次请求之间最长空闲 IdleTimeout；会话关闭同样打断等待。超时按客户端断开处理，由外层兜底关闭连接。
                using (var idle = CancellationTokenSource.CreateLinkedTokenSource(closed))
                {
                    idle.CancelAfter(_limits.IdleTimeout);
                    raw = await reader.ReadHeadAsync(idle.Token);
                }
                if (raw is null || !await One(reader, raw, closed)) return;
            }
        }
        // 不论正常结束、取消还是异常，都关闭出站连接，不留半截的官方连接。
        finally { _upstream?.Dispose(); }
    }

    /// <summary>处理一个请求；返回这条内层连接还能不能接着用。</summary>
    private async Task<bool> One(Http1Reader reader, byte[] raw, CancellationToken closed)
    {
        Http1RequestHead head; string bearer; SiyuRoute route;
        try
        {
            head = Http1RequestHead.Parse(raw, _limits.MaxBodyBytes);
            if (head.Values("Host") is not [var hostValue] || !OfficialTargets.MatchesInnerName(_host, hostValue, true)) throw new Http1Rejection(400, "host");
            if (!SiyuRoutes.TryMatch(_host, head.Method, head.Target, out route)) throw new Http1Rejection(403, "route");
            bearer = head.BearerToken();
        }
        // 不合合同的请求体没法安全跳过：回固定错误后关闭这条内层连接，客户端重连即可。
        catch (Http1Rejection rejection) { await SiyuLocalReply.Write(_inner, rejection.Status, rejection.Code, null, true, closed); return false; }

        ISiyuPermit permit;
        try { permit = await _tunnel.Admit(route, bearer, closed); }
        // 会话层不准入：官方收不到这次请求；请求体还没读，连接不能复用，关闭。
        catch (SiyuRefusal refusal) { await SiyuLocalReply.Write(_inner, refusal.Status, refusal.Code, refusal.Sentence, true, closed); return false; }
        await using (permit)
        {
            var body = await reader.ReadExactAsync(checked((int)head.ContentLength), permit.Cancellation);
            try { permit.Recheck(); await permit.Dispatching(body, permit.Cancellation); }
            // 读完请求体后会话已不可用或身份核对不过：同样一字节都不发给官方；把回给客户端的状态码报给许可，用量记录与客户端看到的一致。连接关闭。
            catch (SiyuRefusal refusal) { permit.Completed(refusal.Status, false); await SiyuLocalReply.Write(_inner, refusal.Status, refusal.Code, refusal.Sentence, true, closed); return false; }
            return await Send(head, route, body, permit);
        }
    }

    /// <summary>写给官方并转发回应；返回内层连接还能不能接着用。</summary>
    private async Task<bool> Send(Http1RequestHead head, SiyuRoute route, byte[] body, ISiyuPermit permit)
    {
        var cancellation = permit.Cancellation;
        if (_upstream is null)
        {
            _upstream = await _connector.Open(_host, cancellation);
            _upstreamReader = new Http1Reader(_upstream);
        }
        await _upstream.WriteAsync(head.WithAuthorization(permit.AccessToken), cancellation);
        if (body.Length > 0) await _upstream.WriteAsync(body, cancellation);
        await _upstream.FlushAsync(cancellation);
        var wroteToClient = false;
        try
        {
            while (true)
            {
                var raw = await _upstreamReader!.ReadHeadAsync(cancellation) ?? throw new EndOfStreamException("官方在回应前关闭了连接。");
                var response = Http1ResponseHead.Parse(raw, head.Method);
                if (response.Status is >= 300 and < 400)
                {
                    // 不跟随、不转发跳转：本地 502，不带 Location；这条出站连接上的回应体不再读，直接丢掉连接。
                    permit.Observe(response.Status);
                    permit.Completed(502, true);
                    DropUpstream();
                    await SiyuLocalReply.Write(_inner, 502, "redirect-refused", "官方要求跳转到别的地址，思域不在合同外跟随，这次请求没有完成。", head.Close, cancellation);
                    return !head.Close;
                }
                if (!response.Interim && route.Purpose == SiyuPurpose.McpServers && response.Status == 200)
                {
                    // 连接器发现：先读全、旁路取出编号并登记，再把头和原字节一起转给客户端，客户端拿到清单时集合已经生效。
                    var wire = await Http1BodyBuffer.Read(_upstreamReader, response, cancellation);
                    if (Http1Entity.Decode(wire, response.Framing, response.Fields) is { } entity && McpServerList.TryReadIds(entity, out var ids))
                        permit.ReportDiscovery(head.WithAuthorization(TemplatePlaceholder), ids);
                    await _inner.WriteAsync(response.Raw, cancellation);
                    await _inner.WriteAsync(wire, cancellation);
                    await _inner.FlushAsync(cancellation);
                    permit.Completed(response.Status, true);
                    permit.Observe(response.Status);
                    if (response.Close || response.Framing == Http1BodyFraming.UntilClose) DropUpstream();
                    return !(head.Close || response.Framing == Http1BodyFraming.UntilClose);
                }
                await _inner.WriteAsync(response.Raw, cancellation);
                await _inner.FlushAsync(cancellation);
                wroteToClient = true;
                if (response.Interim) continue;
                // 有用量记录的用途：回应体写给客户端的同一份字节同时交给用量旁路（只读不改）。
                var tap = permit.Usage is { } usage ? new SiyuUsageTap(usage, response) : null;
                Stream destination = tap is null ? _inner : new TeeStream(_inner, tap);
                switch (response.Framing)
                {
                    case Http1BodyFraming.Length: await _upstreamReader.CopyExactAsync(response.ContentLength, destination, cancellation); break;
                    case Http1BodyFraming.Chunked: await _upstreamReader.CopyChunkedAsync(destination, cancellation); break;
                    case Http1BodyFraming.UntilClose: await _upstreamReader.CopyToEndAsync(destination, cancellation); break;
                    case Http1BodyFraming.None: break;
                }
                tap?.Complete(true);
                permit.Completed(response.Status, true);
                permit.Observe(response.Status);
                if (response.Close) DropUpstream();
                return !(head.Close || response.Framing == Http1BodyFraming.UntilClose);
            }
        }
        // 官方回应不合合同：还没给客户端写过字节就回本地 502，写过了只能断开；两种情况都丢掉出站连接、不重放。
        catch (Http1Rejection rejection)
        {
            DropUpstream();
            if (!wroteToClient) await SiyuLocalReply.Write(_inner, 502, "upstream-" + rejection.Code, "官方的回应无法核对，这次请求的结果未知，没有自动重发。", true, cancellation);
            return false;
        }
    }

    /// <summary>关闭并忘掉当前出站连接，下一个请求重新建连。</summary>
    private void DropUpstream()
    {
        _upstream?.Dispose();
        _upstream = null; _upstreamReader = null;
    }
}
