using System.Text;

namespace Infrastructure.ExternalSources.Siyu;

/// <summary>
/// 一个内层请求的头段：原始字节原样保留，另给只读视图（方法、目标、字段、定界）。
/// 定界合同：请求体只收单个 Content-Length 定长；Transfer-Encoding、Expect、Upgrade 一律拒绝；POST 必须带 Content-Length。
/// </summary>
public sealed class Http1RequestHead
{
    /// <summary>由 <see cref="Parse"/> 构造。</summary>
    private Http1RequestHead(byte[] raw, string method, string target, List<Http1Header> fields, long contentLength, bool close)
    {
        Raw = raw; Method = method; Target = target; Fields = fields; ContentLength = contentLength; Close = close;
    }

    /// <summary>含结尾空行的原始头字节。</summary>
    public byte[] Raw { get; }

    /// <summary>方法（区分大小写，按线上原样）。</summary>
    public string Method { get; }

    /// <summary>request-target 原样（origin-form 或 CONNECT 的 authority-form）。</summary>
    public string Target { get; }

    /// <summary>字段，线上顺序，重复项都在。</summary>
    public IReadOnlyList<Http1Header> Fields { get; }

    /// <summary>请求体字节数（没有体为 0）。</summary>
    public long ContentLength { get; }

    /// <summary>请求要求回应后关闭连接。</summary>
    public bool Close { get; }

    /// <summary>
    /// 按合同解析：请求行必须是 <c>token SP target SP HTTP/1.1</c>（别的版本 505），并判定请求体长度。
    /// <paramref name="maxBody"/> 是该连接允许的最大请求体，超出 413。
    /// </summary>
    public static Http1RequestHead Parse(byte[] raw, long maxBody)
    {
        var (start, fields) = Http1HeadParser.Parse(raw);
        var parts = start.Split(' ');
        if (parts.Length != 3 || !Http1HeadParser.IsToken(parts[0]) || parts[1].Length == 0) throw new Http1Rejection(400, "request-line");
        if (parts[2] != "HTTP/1.1") throw new Http1Rejection(parts[2].StartsWith("HTTP/", StringComparison.Ordinal) ? 505 : 400, parts[2].StartsWith("HTTP/", StringComparison.Ordinal) ? "version" : "request-line");
        if (Http1HeadParser.All(fields, "Transfer-Encoding").Any()) throw new Http1Rejection(501, "transfer-encoding");
        if (Http1HeadParser.All(fields, "Expect").Any()) throw new Http1Rejection(417, "expect");
        if (Http1HeadParser.All(fields, "Upgrade").Any() || Http1HeadParser.ListContains(fields, "Connection", "upgrade")) throw new Http1Rejection(400, "upgrade");
        var length = Http1HeadParser.ContentLength(fields);
        if (length is null && parts[0] == "POST") throw new Http1Rejection(411, "length-required");
        if (length > maxBody) throw new Http1Rejection(413, "body-too-large");
        return new Http1RequestHead(raw, parts[0], parts[1], fields, length ?? 0, Http1HeadParser.ListContains(fields, "Connection", "close"));
    }

    /// <summary>某个名字的全部值（不区分大小写）。</summary>
    public IReadOnlyList<string> Values(string name) => Http1HeadParser.All(Fields, name).Select(field => field.Value).ToArray();

    /// <summary>
    /// 内层请求里客户端出示的个人令牌：Authorization 必须恰好一个且是 <c>Bearer 令牌</c>，令牌只含可见 ASCII；
    /// 同时带了 x-api-key、Proxy-Authorization 或第二个 Authorization 一律 400 <c>authorization</c>，认证来源必须唯一。
    /// </summary>
    public string BearerToken()
    {
        var authorization = Http1HeadParser.All(Fields, "Authorization").ToArray();
        if (authorization.Length != 1 || Http1HeadParser.All(Fields, "x-api-key").Any() || Http1HeadParser.All(Fields, "Proxy-Authorization").Any())
            throw new Http1Rejection(400, "authorization");
        var value = authorization[0].Value;
        if (!value.StartsWith("Bearer ", StringComparison.Ordinal) || value.Length == 7 || value.AsSpan(7).ContainsAnyExcept(_visibleAscii))
            throw new Http1Rejection(400, "authorization");
        return value[7..];
    }

    /// <summary>发往官方的头字节：原始头字节里只把唯一 Authorization 的值换成 <c>Bearer <paramref name="accessToken"/></c>，其他字节一个不动。调用前必须已经过 <see cref="BearerToken"/>。</summary>
    public byte[] WithAuthorization(string accessToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(accessToken);
        if (accessToken.AsSpan().ContainsAnyExcept(_visibleAscii)) throw new ArgumentException("官方令牌含有不能放进请求头的字符。", nameof(accessToken));
        _ = BearerToken();
        var field = Http1HeadParser.All(Fields, "Authorization").Single();
        var value = Encoding.ASCII.GetBytes("Bearer " + accessToken);
        var result = new byte[Raw.Length - field.ValueLength + value.Length];
        Raw.AsSpan(0, field.ValueStart).CopyTo(result);
        value.CopyTo(result.AsSpan(field.ValueStart));
        Raw.AsSpan(field.ValueStart + field.ValueLength).CopyTo(result.AsSpan(field.ValueStart + value.Length));
        return result;
    }

    /// <summary>令牌允许的字符：可见 ASCII（0x21–0x7E）。</summary>
    private static readonly System.Buffers.SearchValues<char> _visibleAscii = System.Buffers.SearchValues.Create(Enumerable.Range(0x21, 0x5E).Select(code => (char)code).ToArray());
}
