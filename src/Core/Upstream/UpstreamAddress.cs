using System.Net;

namespace Core.Upstream;

/// <summary>规范化管理端目的地址并判定地址范围；实际 DNS 解析与建连由基础设施层按这里的规则执行。</summary>
public static class UpstreamAddress
{
    /// <summary>
    /// 校验并规范化管理端填写的上游 API 根地址：只接受 HTTPS（<paramref name="allowPrivate"/> 为 true 时才放行 HTTP），
    /// 拒绝用户名密码、片段、控制字符、反斜杠、超过 2048 字符、查询参数里的凭据类字段、转义或相对路径段。
    /// 去掉末尾的 <c>/v1</c> 后返回以 <c>/</c> 结尾、保留原查询串的地址，便于再拼接具体接口；不合法时抛出 <see cref="InvalidOperationException"/>。
    /// 这里只校验文字形式，不做 DNS 解析，也不判断目标是否真的位于私网。
    /// </summary>
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

    /// <summary>
    /// 把允许的接口路由拼到规范化后的上游根地址上，并合并根地址自带的查询串与本次请求的 <paramref name="query"/>（可带或不带前导 <c>?</c>）。
    /// 路由只允许 messages、count_tokens、models 和 /api/hello 四个白名单，其余抛出 <see cref="InvalidOperationException"/>，防止中转被用来访问任意路径。
    /// </summary>
    public static Uri Endpoint(UpstreamOptions options, string route, string query = "")
    {
        if (route is not ("/v1/messages" or "/v1/messages/count_tokens" or "/v1/models" or "/api/hello"))
            throw new InvalidOperationException("不支持的上游接口。");
        var baseUri = Normalize(options.BaseUrl, options.AllowPrivateNetwork);
        var separator = baseUri.Query.Length == 0 ? "?" : "&";
        return new Uri(baseUri.GetLeftPart(UriPartial.Path) + route.TrimStart('/') + baseUri.Query + (query.Length == 0 ? "" : separator + query.TrimStart('?')));
    }

    /// <summary>校验出站代理地址：只接受不超过 2048 字符、不含账号密码、查询参数或片段的 HTTP 根地址（路径必须是 <c>/</c>），不合法时抛出 <see cref="InvalidOperationException"/>。</summary>
    public static Uri Proxy(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "http" || uri.UserInfo.Length != 0
            || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/" || value.Length > 2048)
            throw new InvalidOperationException("出站代理只接受本机/私网 HTTP CONNECT 根地址，不含账号、密码或查询参数。");
        return uri;
    }

    /// <summary>判断是否为本机或私网地址：回环地址、IPv4 的 10/8、172.16/12、192.168/16，以及 IPv6 唯一本地地址 fc00::/7；IPv4 映射的 IPv6 地址先还原为 IPv4 再判断。链路本地地址不算私网。</summary>
    public static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 ? bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168
            : (bytes[0] & 0xfe) == 0xfc;
    }

    /// <summary>
    /// 判断是否为可以直连的公网地址，用于阻止借上游地址访问内网或保留地址。私网地址一律不算公网；
    /// IPv6 只接受 2000::/3 全球单播并排除文档段 2001:db8::/32；IPv4 只接受首字节 1–223，并排除 169.254（链路本地）、
    /// 100.64–100.127（运营商级 NAT）、192.0.*、198.18/198.19/198.51.* 和 203.0.113.* 等测试或保留段。
    /// </summary>
    public static bool IsPublic(IPAddress address)
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
}
