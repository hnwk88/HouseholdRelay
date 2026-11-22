namespace Infrastructure.ExternalSources.Siyu;

/// <summary>
/// 上游回应的头段：原始字节原样保留，另给状态码、字段与按 RFC 9112 §6.3 判出的定界方式。
/// Transfer-Encoding 只认唯一的 <c>chunked</c>；与 Content-Length 同时出现、101 升级都按走私或越界处理，抛 <see cref="Http1Rejection"/>（调用方关闭两端）。
/// </summary>
public sealed class Http1ResponseHead
{
    /// <summary>由 <see cref="Parse"/> 构造。</summary>
    private Http1ResponseHead(byte[] raw, int status, List<Http1Header> fields, Http1BodyFraming framing, long contentLength, bool close)
    {
        Raw = raw; Status = status; Fields = fields; Framing = framing; ContentLength = contentLength; Close = close;
    }

    /// <summary>含结尾空行的原始头字节。</summary>
    public byte[] Raw { get; }

    /// <summary>三位状态码。</summary>
    public int Status { get; }

    /// <summary>字段，线上顺序。</summary>
    public IReadOnlyList<Http1Header> Fields { get; }

    /// <summary>消息体定界方式。</summary>
    public Http1BodyFraming Framing { get; }

    /// <summary><see cref="Http1BodyFraming.Length"/> 时的字节数，其他为 0。</summary>
    public long ContentLength { get; }

    /// <summary>转发完这条回应后要关闭客户端内层连接（上游要求关闭，或读到关闭为止）。</summary>
    public bool Close { get; }

    /// <summary>是不是中间回应（1xx，101 除外）：原样转发后接着读下一个回应头。</summary>
    public bool Interim => Status is >= 100 and < 200;

    /// <summary>按合同解析；<paramref name="requestMethod"/> 用来判 HEAD 无体。</summary>
    public static Http1ResponseHead Parse(byte[] raw, string requestMethod)
    {
        ArgumentNullException.ThrowIfNull(requestMethod);
        var (start, fields) = Http1HeadParser.Parse(raw);
        if (start.Length < 12 || !start.StartsWith("HTTP/1.1 ", StringComparison.Ordinal) || !char.IsAsciiDigit(start[9]) || !char.IsAsciiDigit(start[10]) || !char.IsAsciiDigit(start[11])
            || start.Length > 12 && start[12] != ' ') throw new Http1Rejection(502, "status-line");
        var status = (start[9] - '0') * 100 + (start[10] - '0') * 10 + (start[11] - '0');
        if (status is < 100 or > 599) throw new Http1Rejection(502, "status-line");
        if (status == 101) throw new Http1Rejection(502, "upgrade");
        var close = Http1HeadParser.ListContains(fields, "Connection", "close");
        var encodings = Http1HeadParser.All(fields, "Transfer-Encoding").ToArray();
        var length = Http1HeadParser.ContentLength(fields);
        if (encodings.Length > 0 && length is not null) throw new Http1Rejection(502, "transfer-encoding-and-length");
        if (status is >= 100 and < 200 || status is 204 or 304 || requestMethod == "HEAD") return new Http1ResponseHead(raw, status, fields, Http1BodyFraming.None, 0, close);
        if (encodings.Length > 0)
        {
            if (encodings.Length != 1 || !string.Equals(encodings[0].Value, "chunked", StringComparison.OrdinalIgnoreCase)) throw new Http1Rejection(502, "transfer-encoding");
            return new Http1ResponseHead(raw, status, fields, Http1BodyFraming.Chunked, 0, close);
        }
        if (length is not null) return new Http1ResponseHead(raw, status, fields, length == 0 ? Http1BodyFraming.None : Http1BodyFraming.Length, length.Value, close);
        return new Http1ResponseHead(raw, status, fields, Http1BodyFraming.UntilClose, 0, true);
    }
}
