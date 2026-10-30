using System.Net;
using System.Net.Sockets;
using System.Text;
using Application.Interfaces.Upstream;
using Core.Upstream;

namespace Infrastructure.ExternalSources.Upstream;

/// <summary>实际建连时按地址规则校验并固定 DNS 解析结果；经代理时也只连接已检查的目标 IP。</summary>
public static class UpstreamDialer
{
    /// <summary>解析主机名并逐个校验地址：只要有一个地址不在允许范围（未开放内网时出现内网地址，或明文 http 上游出现公网地址）就整体拒绝，防止 DNS 返回混合结果绕过限制。</summary>
    private static async Task<IPAddress[]> Resolve(string host, bool allowPrivate, bool privateOnly, CancellationToken cancellation)
    {
        var addresses = await Dns.GetHostAddressesAsync(host, cancellation);
        if (addresses.Length == 0 || addresses.Any(address => !(UpstreamAddress.IsPrivate(address) && allowPrivate || UpstreamAddress.IsPublic(address) && !privateOnly)))
            throw new HttpRequestException("目的地址超出允许范围。");
        return addresses;
    }

    /// <summary>
    /// 作为 <see cref="SocketsHttpHandler.ConnectCallback"/> 为上游建立底层连接：先按全局设置解析代理（启用却取不到时抛出 <see cref="UpstreamProxyUnavailableException"/>，不发任何连接），
    /// 再校验目标地址；直连时连已校验的 IP，走代理时连到同样校验过的内网代理，再用 CONNECT 指向已校验的目标 IP 并检查代理回复 200。返回的流由 HTTP 处理器持有并释放。
    /// </summary>
    public static async ValueTask<Stream> Connect(SocketsHttpConnectionContext context, UpstreamOptions options, IUpstreamProxy proxySettings, CancellationToken cancellation)
    {
        var target = context.DnsEndPoint;
        var plain = options.BaseUrl.StartsWith("http://", StringComparison.Ordinal);
        var route = proxySettings.Resolve(new UriBuilder(plain ? "http" : "https", target.Host, target.Port).Uri);
        var addresses = await Resolve(target.Host, options.AllowPrivateNetwork, plain, cancellation);
        if (route.Address is null) return await Open(addresses, target.Port, cancellation);
        var proxy = UpstreamAddress.Proxy(route.Address.AbsoluteUri);
        var proxyAddresses = await Resolve(proxy.DnsSafeHost, true, true, cancellation);
        var stream = await Open(proxyAddresses, proxy.Port, cancellation);
        try
        {
            // 代理也连接已检查的目标 IP，不让代理重新解析目标主机而绕过地址范围限制。
            var address = addresses[0]; var authority = address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]:{target.Port}" : $"{address}:{target.Port}";
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT {authority} HTTP/1.1\r\nHost: {authority}\r\n\r\n"), cancellation);
            var header = new List<byte>(); var one = new byte[1];
            while (header.Count < 8192)
            {
                if (await stream.ReadAsync(one, cancellation) == 0) throw new HttpRequestException("代理连接被关闭。");
                header.Add(one[0]);
                if (header.Count >= 4 && header.TakeLast(4).SequenceEqual(new byte[] { 13, 10, 13, 10 })) break;
            }
            var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
            if (header.Count >= 8192 || !lines[0].StartsWith("HTTP/1.", StringComparison.Ordinal) || lines[0].Split(' ').ElementAtOrDefault(1) != "200")
                throw new HttpRequestException("代理未接受 CONNECT 请求。");
            return stream;
        }
        // 代理握手任何失败（含取消）都先关闭已打开的代理连接再原样抛出，避免泄漏套接字。
        catch { stream.Dispose(); throw; }
    }

    /// <summary>按顺序尝试连接已校验的地址列表，第一个连上的地址返回网络流（流拥有套接字）；全部失败时抛出 <see cref="HttpRequestException"/>，内部异常是最后一个套接字错误。</summary>
    private static async Task<Stream> Open(IPAddress[] addresses, int port, CancellationToken cancellation)
    {
        SocketException? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try { await socket.ConnectAsync(new IPEndPoint(address, port), cancellation); return new NetworkStream(socket, ownsSocket: true); }
            // 单个地址连不上只释放这个套接字，继续尝试下一个已校验地址。
            catch (SocketException error) { socket.Dispose(); last = error; }
            // 取消等其他异常不再重试：释放套接字后原样抛出，让取消及时传到调用方。
            catch { socket.Dispose(); throw; }
        }
        // 带上最后一个套接字错误作内部异常，上游尝试记录才分得出拒绝连接还是超时；返回给客户端的仍是固定提示。
        throw new HttpRequestException("无法连接已验证的目的地址。", last);
    }
}
