using System.Net;
using System.Net.Sockets;
using System.Text;

namespace HouseholdRelay;

/// <summary>规范化管理端目的地址，并在实际建连时校验和固定 DNS 解析结果。</summary>
public static class UpstreamAddress
{
    public static Uri Normalize(string value, bool allowPrivate)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")
            || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0
            || value!.Length > 2048 || value.Any(char.IsControl) || value.Contains('\\'))
            throw new InvalidOperationException("上游地址必须是 HTTP/HTTPS 地址，不能含用户名、密码、控制字符或片段。");
        if (uri.Scheme == "http" && !allowPrivate) throw new InvalidOperationException("默认只允许 HTTPS；本机或私网 HTTP 需要明确启用。");
        if (uri.Query.Length != 0 && uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Any(part => new[] { "key", "token", "secret", "password", "auth", "credential", "signature" }.Any(word => Uri.UnescapeDataString(part.Split('=', 2)[0]).Contains(word, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException("上游地址查询参数不能包含凭据或认证字段。");
        var escapedPath = uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
        if (escapedPath.Contains('%') || escapedPath.Split('/').Any(segment => segment is "." or ".."))
            throw new InvalidOperationException("路径前缀不能含转义或相对路径段，请填写明确的 API 根地址。");
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) path = path[..^3];
        return new Uri(uri.GetLeftPart(UriPartial.Authority) + path + "/" + uri.Query);
    }

    public static Uri Endpoint(UpstreamOptions options, string route, string query = "")
    {
        if (route is not ("/v1/messages" or "/v1/messages/count_tokens" or "/v1/models" or "/api/hello"))
            throw new InvalidOperationException("不支持的上游接口。");
        var baseUri = Normalize(options.BaseUrl, options.AllowPrivateNetwork);
        var separator = baseUri.Query.Length == 0 ? "?" : "&";
        return new Uri(baseUri.GetLeftPart(UriPartial.Path) + route.TrimStart('/') + baseUri.Query + (query.Length == 0 ? "" : separator + query.TrimStart('?')));
    }

    public static Uri Proxy(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "http" || uri.UserInfo.Length != 0
            || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/" || value.Length > 2048)
            throw new InvalidOperationException("出站代理只接受本机/私网 HTTP CONNECT 根地址，不含账号、密码或查询参数。");
        return uri;
    }

    internal static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 ? bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168
            : (bytes[0] & 0xfe) == 0xfc;
    }

    internal static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IsPrivate(address)) return false;
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 16) return (bytes[0] & 0xe0) == 0x20 && !(bytes[0] == 0x20 && bytes[1] == 1 && bytes[2] == 0x0d && bytes[3] == 0xb8);
        return bytes[0] is > 0 and < 224 && !(bytes[0] == 169 && bytes[1] == 254)
            && !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
            && !(bytes[0] == 192 && bytes[1] == 0) && !(bytes[0] == 198 && bytes[1] is 18 or 19 or 51)
            && !(bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113);
    }

    private static async Task<IPAddress[]> Resolve(string host, bool allowPrivate, bool privateOnly, CancellationToken cancellation)
    {
        var addresses = await Dns.GetHostAddressesAsync(host, cancellation);
        if (addresses.Length == 0 || addresses.Any(address => !(IsPrivate(address) && allowPrivate || IsPublic(address) && !privateOnly)))
            throw new HttpRequestException("目的地址超出允许范围。");
        return addresses;
    }

    internal static async ValueTask<Stream> Connect(SocketsHttpConnectionContext context, UpstreamOptions options, CancellationToken cancellation)
    {
        var target = context.DnsEndPoint;
        var addresses = await Resolve(target.Host, options.AllowPrivateNetwork, options.BaseUrl.StartsWith("http://", StringComparison.Ordinal), cancellation);
        if (options.ProxyUrl is null) return await Open(addresses, target.Port, cancellation);
        var proxy = Proxy(options.ProxyUrl);
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
        catch { stream.Dispose(); throw; }
    }

    private static async Task<Stream> Open(IPAddress[] addresses, int port, CancellationToken cancellation)
    {
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try { await socket.ConnectAsync(new IPEndPoint(address, port), cancellation); return new NetworkStream(socket, ownsSocket: true); }
            catch (SocketException) { socket.Dispose(); }
            catch { socket.Dispose(); throw; }
        }
        throw new HttpRequestException("无法连接已验证的目的地址。");
    }
}
