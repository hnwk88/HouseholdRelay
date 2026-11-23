using System.IO.Pipelines;
using System.Net.Security;
using System.Security.Authentication;
using System.Text;
using Application.Interfaces.Siyu;
using Application.Logging;
using Core.Siyu;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;

namespace Infrastructure.ExternalSources.Siyu;

/// <summary>
/// 思域代理入口的一条外层连接（外层 TLS 已由 Kestrel 用站点证书终止）：只认一个 HTTP/1.1 CONNECT，目标必须是两个官方 authority 之一、Host 与目标逐字相同，
/// <c>Proxy-Authorization: Basic 会话编号:票据</c> 换隧道；回 200 后在同一条流上用代理 CA 签的叶证书终止内层 TLS（SNI 必须是同一个主机，ALPN 只报 http/1.1），
/// 再交给 <see cref="SiyuInnerExchange"/> 逐请求处理。任何一步不合合同都回本地错误并关闭；票据、令牌不进日志。
/// </summary>
public sealed class SiyuProxyConnectionHandler : ConnectionHandler
{
    private readonly ISiyuGate _gate;
    private readonly IOfficialConnector _connector;
    private readonly ISiyuProxyCertificates _certificates;
    private readonly SiyuProxyLimits _limits;
    private readonly ILogger<SiyuProxyConnectionHandler> _logger;

    /// <summary>全部依赖必填，构造时当场判空。</summary>
    public SiyuProxyConnectionHandler(ISiyuGate gate, IOfficialConnector connector, ISiyuProxyCertificates certificates, SiyuProxyLimits limits, ILogger<SiyuProxyConnectionHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(gate); ArgumentNullException.ThrowIfNull(connector); ArgumentNullException.ThrowIfNull(certificates);
        ArgumentNullException.ThrowIfNull(limits); ArgumentNullException.ThrowIfNull(logger);
        _gate = gate; _connector = connector; _certificates = certificates; _limits = limits; _logger = logger;
    }

    /// <summary>Kestrel 每接一条外层连接调用一次。</summary>
    public override async Task OnConnectedAsync(ConnectionContext connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var outer = new DuplexStream(connection.Transport.Input.AsStream(), connection.Transport.Output.AsStream());
        try { await ServeAsync(outer, connection.ConnectionClosed); }
        // 客户端断开、会话关闭、超时、TLS 握手失败、对端中途断开都是这条连接的正常结局：连接随 using 关闭，不记错误。
        catch (Exception error) when (error is OperationCanceledException or IOException or AuthenticationException or EndOfStreamException) { }
        // 其他没预料到的异常只记类型链和阶段（不带消息，可能含凭据），连接随 using 关闭。
        catch (Exception error) { RelayLog.Unexpected(_logger, error, "siyu:proxy"); }
    }

    /// <summary>外层：读 CONNECT、认证、回 200、内层 TLS、进内层循环。</summary>
    private async Task ServeAsync(Stream outer, CancellationToken aborted)
    {
        var reader = new Http1Reader(outer);
        byte[]? raw;
        // 头段超时令牌挂在连接断开之下：断开或超时都中止读头。
        using (var headTimeout = CancellationTokenSource.CreateLinkedTokenSource(aborted))
        {
            // 读 CONNECT 头最多 HeadTimeout，防止慢速连接占住入口。
            headTimeout.CancelAfter(_limits.HeadTimeout);
            raw = await reader.ReadHeadAsync(headTimeout.Token);
        }
        if (raw is null) return;
        Http1RequestHead head;
        try { head = Http1RequestHead.Parse(raw, 0); }
        // 头段不合合同：回固定状态码和原因码后关闭，不再读后续字节。
        catch (Http1Rejection rejection) { await SiyuLocalReply.WriteAsync(outer, rejection.Status, rejection.Code, null, true, aborted); return; }
        if (head.Method != "CONNECT") { await SiyuLocalReply.WriteAsync(outer, 405, "method", null, true, aborted); return; }
        if (!OfficialTargets.TryParseConnectTarget(head.Target, out var host)) { await SiyuLocalReply.WriteAsync(outer, 403, "target", "思域只连接官方的两个地址，这个目标不在合同内。", true, aborted); return; }
        if (head.Values("Host") is not [var hostValue] || hostValue != head.Target) { await SiyuLocalReply.WriteAsync(outer, 400, "host", null, true, aborted); return; }
        using var tunnel = Credentials(head) is var (sessionId, ticket) ? _gate.OpenTunnel(sessionId, ticket, host) : null;
        if (tunnel is null) { await SiyuLocalReply.WriteAsync(outer, 407, "proxy-authentication", "思域会话无效或已结束，请重新启动思域。", true, aborted, proxyChallenge: true); return; }
        // 客户端必须等 200 之后才开始内层握手；提前到的字节说明对端不按 CONNECT 语义走，关闭。
        if (reader.HasBufferedBytes) { await SiyuLocalReply.WriteAsync(outer, 400, "early-data", null, true, aborted); return; }
        await outer.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), aborted);
        await outer.FlushAsync(aborted);

        // closed：客户端断开或会话关闭（停止、撤销、维护、租约到期）任一发生，就取消这条隧道上的全部读写。
        using var closed = CancellationTokenSource.CreateLinkedTokenSource(aborted, tunnel.Closed);
        await using var inner = new SslStream(outer, leaveInnerStreamOpen: true);
        // 握手用自己的超时令牌，挂在 closed 下，任何一方取消都中止握手。
        using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(closed.Token))
        {
            // 握手超时只打断握手本身；会话关闭或客户端断开也经 closed 传进来。
            handshake.CancelAfter(_limits.HandshakeTimeout);
            await inner.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = _certificates.Leaf(host),
                ApplicationProtocols = [SslApplicationProtocol.Http11],
                ClientCertificateRequired = false,
                CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck,
            }, handshake.Token);
        }
        // 内层 SNI 必须就是这条隧道的官方主机；协商出的应用协议只能是 http/1.1 或客户端没报 ALPN。
        if (!OfficialTargets.MatchesInnerName(host, inner.TargetHostName, false)) return;
        if (inner.NegotiatedApplicationProtocol != default && inner.NegotiatedApplicationProtocol != SslApplicationProtocol.Http11) return;
        await new SiyuInnerExchange(inner, host, tunnel, _connector, _limits).RunAsync(closed.Token);
    }

    /// <summary>从唯一的 <c>Proxy-Authorization: Basic</c> 取出「会话编号:票据」；缺失、重复、格式不对返回 null。</summary>
    private static (string SessionId, string Ticket)? Credentials(Http1RequestHead head)
    {
        if (head.Values("Proxy-Authorization") is not [var value] || !value.StartsWith("Basic ", StringComparison.Ordinal)) return null;
        byte[] decoded;
        try { decoded = Convert.FromBase64String(value[6..]); }
        // base64 本身就坏了：当作没带凭据，回 407。
        catch (FormatException) { return null; }
        var text = Encoding.UTF8.GetString(decoded);
        var colon = text.IndexOf(':');
        return colon <= 0 || colon == text.Length - 1 ? null : (text[..colon], text[(colon + 1)..]);
    }
}
