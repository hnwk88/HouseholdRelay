using System.IO.Compression;

namespace Infrastructure.ExternalSources.Siyu;

/// <summary>
/// 把已经按合同定界、读全了的回应体线上字节还原成实体，只用于旁路检查（线上照发原字节）：先解开 chunked 分块，再按唯一的
/// <c>Content-Encoding</c>（identity、gzip、deflate、br）解压；解压后超过上限、编码不认识或有多个编码时返回 null，调用方按「看不懂」处理。
/// </summary>
public static class Http1Entity
{
    /// <summary>解码后最多这么多字节（发现回应只是一份连接器清单）。</summary>
    public const int MaxDecodedBytes = 4 * 1024 * 1024;

    /// <summary>还原实体；看不懂返回 null。<paramref name="wire"/> 是回应头之后的全部体字节（chunked 时含分块框架与 trailer）。</summary>
    public static byte[]? Decode(byte[] wire, Http1BodyFraming framing, IReadOnlyList<Http1Header> fields)
    {
        ArgumentNullException.ThrowIfNull(wire); ArgumentNullException.ThrowIfNull(fields);
        var body = framing == Http1BodyFraming.Chunked ? Dechunk(wire) : wire;
        if (body is null) return null;
        var encodings = Http1HeadParser.All(fields, "Content-Encoding").Select(field => field.Value.Trim().ToLowerInvariant()).ToArray();
        if (encodings.Length == 0) return body.Length <= MaxDecodedBytes ? body : null;
        if (encodings is not [var encoding] || encoding.Contains(',', StringComparison.Ordinal)) return null;
        return encoding switch
        {
            "identity" => body.Length <= MaxDecodedBytes ? body : null,
            "gzip" => Inflate(body, source => new GZipStream(source, CompressionMode.Decompress)),
            "deflate" => Inflate(body, source => new ZLibStream(source, CompressionMode.Decompress)),
            "br" => Inflate(body, source => new BrotliStream(source, CompressionMode.Decompress)),
            _ => null,
        };
    }

    /// <summary>解开分块：块长十六进制（可带扩展）、块数据、CRLF，直到 0 长块；trailer 不要。框架已由读取时核对过，这里仍按不合即 null 处理。</summary>
    private static byte[]? Dechunk(byte[] wire)
    {
        var output = new MemoryStream();
        var position = 0;
        while (true)
        {
            var lineEnd = wire.AsSpan(position).IndexOf("\r\n"u8);
            if (lineEnd < 0) return null;
            long size;
            try { size = Http1Reader.ChunkSize(wire.AsSpan(position, lineEnd + 2)); }
            // 块长不合法：读取时本该已经拒绝，这里不再信任，按看不懂处理。
            catch (Http1Rejection) { return null; }
            position += lineEnd + 2;
            if (size == 0) return output.ToArray();
            if (size > wire.Length - position - 2 || output.Length + size > MaxDecodedBytes) return null;
            output.Write(wire, position, (int)size);
            position += (int)size;
            if (!wire.AsSpan(position).StartsWith("\r\n"u8)) return null;
            position += 2;
        }
    }

    /// <summary>有界解压：超过 <see cref="MaxDecodedBytes"/> 或数据损坏返回 null。</summary>
    private static byte[]? Inflate(byte[] body, Func<Stream, Stream> open)
    {
        try
        {
            using var source = open(new MemoryStream(body));
            var output = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (output.Length + read > MaxDecodedBytes) return null;
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
        // 压缩数据损坏：按看不懂处理，不抛给转发路径。
        catch (InvalidDataException) { return null; }
    }
}
