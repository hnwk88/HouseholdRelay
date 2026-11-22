using System.Text;

namespace Infrastructure.ExternalSources.Siyu;

/// <summary>
/// 头段的严格解析（请求与回应共用）：输入是含结尾空行的完整原始头字节，输出起始行与按线上顺序排列的字段。
/// 只接受 CRLF 行尾；裸 CR、裸 LF、obs-fold、非 token 的字段名、冒号前空白、控制字符与 0x80 以上字节一律拒绝。
/// </summary>
public static class Http1HeadParser
{
    /// <summary>头段（含起始行与结尾空行）最多字节数。</summary>
    public const int MaxHeadBytes = 32 * 1024;

    /// <summary>头段最多行数（含起始行）。</summary>
    public const int MaxLines = 128;

    /// <summary>拆出起始行与字段；不合合同时抛 <see cref="Http1Rejection"/>。</summary>
    public static (string StartLine, List<Http1Header> Fields) Parse(byte[] raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (raw.Length > MaxHeadBytes) throw new Http1Rejection(431, "head-too-large");
        CheckLineEndings(raw);
        var fields = new List<Http1Header>();
        string? start = null;
        var position = 0; var lines = 0;
        while (true)
        {
            var end = IndexOfCrLf(raw, position);
            if (end < 0) throw new Http1Rejection(400, "head-truncated");
            if (++lines > MaxLines) throw new Http1Rejection(431, "head-too-large");
            if (end == position)
            {
                if (start is null) throw new Http1Rejection(400, "request-line");
                if (end + 2 != raw.Length) throw new Http1Rejection(400, "head-trailing-bytes");
                return (start, fields);
            }
            if (start is null)
            {
                for (var i = position; i < end; i++) if (raw[i] is < 0x20 or > 0x7E) throw new Http1Rejection(400, "request-line");
                start = Encoding.ASCII.GetString(raw, position, end - position);
            }
            else fields.Add(ParseField(raw, position, end));
            position = end + 2;
        }
    }

    /// <summary>一行字段：名字是 token、紧跟冒号；值去掉两侧空格与 HTAB，只许 HTAB 与可见 ASCII 加空格。</summary>
    public static Http1Header ParseField(byte[] raw, int start, int end)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (raw[start] is (byte)' ' or (byte)'\t') throw new Http1Rejection(400, "obs-fold");
        var colon = -1;
        for (var i = start; i < end; i++) if (raw[i] == (byte)':') { colon = i; break; }
        if (colon <= start) throw new Http1Rejection(400, "header-name");
        for (var i = start; i < colon; i++) if (!IsTokenChar(raw[i])) throw new Http1Rejection(400, "header-name");
        var valueStart = colon + 1; var valueEnd = end;
        while (valueStart < valueEnd && raw[valueStart] is (byte)' ' or (byte)'\t') valueStart++;
        while (valueEnd > valueStart && raw[valueEnd - 1] is (byte)' ' or (byte)'\t') valueEnd--;
        for (var i = valueStart; i < valueEnd; i++) if (raw[i] != (byte)'\t' && raw[i] is < 0x20 or > 0x7E) throw new Http1Rejection(400, "header-value");
        return new Http1Header(Encoding.ASCII.GetString(raw, start, colon - start), Encoding.ASCII.GetString(raw, valueStart, valueEnd - valueStart), valueStart, valueEnd - valueStart);
    }

    /// <summary>RFC 9110 tchar。</summary>
    public static bool IsTokenChar(byte value) =>
        value is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9'
            or (byte)'!' or (byte)'#' or (byte)'$' or (byte)'%' or (byte)'&' or (byte)'\'' or (byte)'*' or (byte)'+' or (byte)'-' or (byte)'.' or (byte)'^' or (byte)'_' or (byte)'`' or (byte)'|' or (byte)'~';

    /// <summary>字符串是不是非空 token。</summary>
    public static bool IsToken(string value) => value.Length > 0 && value.All(c => c < 0x80 && IsTokenChar((byte)c));

    /// <summary>每个 CR 后面必须是 LF，每个 LF 前面必须是 CR。</summary>
    public static void CheckLineEndings(ReadOnlySpan<byte> raw)
    {
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] == (byte)'\r' && (i + 1 >= raw.Length || raw[i + 1] != (byte)'\n')) throw new Http1Rejection(400, "bare-line-ending");
            if (raw[i] == (byte)'\n' && (i == 0 || raw[i - 1] != (byte)'\r')) throw new Http1Rejection(400, "bare-line-ending");
        }
    }

    /// <summary>字段按名字（不区分大小写）出现的全部值，线上顺序。</summary>
    public static IEnumerable<Http1Header> All(IReadOnlyList<Http1Header> fields, string name) => fields.Where(field => field.Is(name));

    /// <summary>逗号分隔的列表里有没有某个 token（不区分大小写，两侧空白忽略）。</summary>
    public static bool ListContains(IReadOnlyList<Http1Header> fields, string name, string token) =>
        All(fields, name).SelectMany(field => field.Value.Split(',')).Any(item => string.Equals(item.Trim(' ', '\t'), token, StringComparison.OrdinalIgnoreCase));

    /// <summary>Content-Length：没有返回 null；出现两次以上、逗号列表、非纯十进制或超出 long 一律 400。</summary>
    public static long? ContentLength(IReadOnlyList<Http1Header> fields)
    {
        var values = All(fields, "Content-Length").ToArray();
        if (values.Length == 0) return null;
        if (values.Length > 1) throw new Http1Rejection(400, "content-length");
        var text = values[0].Value;
        if (text.Length is 0 or > 18 || !text.All(char.IsAsciiDigit)) throw new Http1Rejection(400, "content-length");
        return long.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>从 <paramref name="from"/> 起找下一个 CRLF 的位置，找不到返回 -1。</summary>
    private static int IndexOfCrLf(byte[] raw, int from)
    {
        var index = raw.AsSpan(from).IndexOf("\r\n"u8);
        return index < 0 ? -1 : from + index;
    }
}
