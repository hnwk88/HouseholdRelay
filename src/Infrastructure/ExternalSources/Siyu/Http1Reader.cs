namespace Infrastructure.ExternalSources.Siyu;

/// <summary>
/// 一条 HTTP/1.1 字节流上的带缓冲读取器：按头段、定长体、逐块体、读到关闭四种方式取字节，读到的字节原样交出或原样写到目标流。
/// 一条连接只用一个实例；读多了的字节留在缓冲里给下一个报文（流水线按顺序处理）。不是线程安全的。
/// </summary>
public sealed class Http1Reader
{
    /// <summary>块长度行（含块扩展）最多字节数。</summary>
    public const int MaxChunkLineBytes = 4096;

    /// <summary>末块之后 trailer 段最多字节数。</summary>
    public const int MaxTrailerBytes = 32 * 1024;

    private readonly Stream _source;
    private readonly byte[] _buffer;
    private int _start;
    private int _end;

    /// <summary><paramref name="source"/> 必填；缓冲至少要能放下一个完整头段。</summary>
    public Http1Reader(Stream source, int bufferSize = 64 * 1024)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(bufferSize, Http1HeadParser.MaxHeadBytes + 4);
        _source = source; _buffer = new byte[bufferSize];
    }

    /// <summary>缓冲里还有没交出去的字节（例如流水线里的下一个请求）。</summary>
    public bool HasBufferedBytes => _end > _start;

    /// <summary>
    /// 读一个完整头段（到第一个空行为止，含它）。连接在报文边界干净关闭返回 null；头段中途断开抛 <see cref="EndOfStreamException"/>；
    /// 裸 CR、裸 LF 读到就拒绝，不等读完；超过 <see cref="Http1HeadParser.MaxHeadBytes"/> 拒绝 431。
    /// </summary>
    public async Task<byte[]?> ReadHeadAsync(CancellationToken cancellation)
    {
        var scanned = 0;
        while (true)
        {
            for (var i = _start + scanned; i < _end; i++)
            {
                var value = _buffer[i];
                if (value == (byte)'\n')
                {
                    if (i == _start || _buffer[i - 1] != (byte)'\r') throw new Http1Rejection(400, "bare-line-ending");
                    if (i - _start >= 3 && _buffer[i - 2] == (byte)'\n' && _buffer[i - 3] == (byte)'\r')
                    {
                        var head = _buffer.AsSpan(_start, i + 1 - _start).ToArray();
                        _start = i + 1;
                        return head;
                    }
                }
                else if (i > _start && _buffer[i - 1] == (byte)'\r') throw new Http1Rejection(400, "bare-line-ending");
                if (i + 1 - _start > Http1HeadParser.MaxHeadBytes) throw new Http1Rejection(431, "head-too-large");
            }
            scanned = _end - _start;
            if (!await FillAsync(cancellation))
            {
                if (scanned == 0) return null;
                throw new EndOfStreamException("头段没读完连接就关闭了。");
            }
        }
    }

    /// <summary>读恰好 <paramref name="count"/> 个字节并交出（用于要先整段核验再发送的请求体）；不够就抛 <see cref="EndOfStreamException"/>。</summary>
    public async Task<byte[]> ReadExactAsync(int count, CancellationToken cancellation)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var result = new byte[count]; var filled = 0;
        while (filled < count)
        {
            if (_end == _start && !await FillAsync(cancellation)) throw new EndOfStreamException("消息体没读完连接就关闭了。");
            var take = Math.Min(count - filled, _end - _start);
            _buffer.AsSpan(_start, take).CopyTo(result.AsSpan(filled));
            _start += take; filled += take;
        }
        return result;
    }

    /// <summary>把恰好 <paramref name="count"/> 个字节原样写到 <paramref name="destination"/>，每写一段就冲刷（流式回应边到边转）；不够就抛 <see cref="EndOfStreamException"/>。</summary>
    public async Task CopyExactAsync(long count, Stream destination, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(destination); ArgumentOutOfRangeException.ThrowIfNegative(count);
        while (count > 0)
        {
            if (_end == _start && !await FillAsync(cancellation)) throw new EndOfStreamException("消息体没读完连接就关闭了。");
            var take = (int)Math.Min(count, _end - _start);
            await destination.WriteAsync(_buffer.AsMemory(_start, take), cancellation);
            await destination.FlushAsync(cancellation);
            _start += take; count -= take;
        }
    }

    /// <summary>
    /// 逐块原样转发 chunked 消息体，直到末块和 trailer 段结束：块长行只许 1–16 位十六进制，后面可跟有界块扩展；
    /// 每块数据后必须紧跟 CRLF；trailer 段每行按头字段严格校验。任何不合合同抛 <see cref="Http1Rejection"/>，中途断开抛 <see cref="EndOfStreamException"/>。
    /// </summary>
    public async Task CopyChunkedAsync(Stream destination, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(destination);
        while (true)
        {
            var line = await ReadLineAsync(MaxChunkLineBytes, cancellation);
            var size = ChunkSize(line);
            await destination.WriteAsync(line, cancellation);
            if (size == 0) break;
            await CopyExactAsync(size, destination, cancellation);
            var end = await ReadExactAsync(2, cancellation);
            if (end[0] != (byte)'\r' || end[1] != (byte)'\n') throw new Http1Rejection(502, "chunk-terminator");
            await destination.WriteAsync(end, cancellation);
            await destination.FlushAsync(cancellation);
        }
        var trailers = 0;
        while (true)
        {
            var line = await ReadLineAsync(MaxTrailerBytes, cancellation);
            trailers += line.Length;
            if (trailers > MaxTrailerBytes) throw new Http1Rejection(502, "trailers-too-large");
            if (line.Length > 2) Http1HeadParser.ParseField(line, 0, line.Length - 2);
            await destination.WriteAsync(line, cancellation);
            if (line.Length == 2) break;
        }
        await destination.FlushAsync(cancellation);
    }

    /// <summary>把剩下的字节一直原样写到 <paramref name="destination"/>，直到对端关闭（回应既无长度也非 chunked 时）。</summary>
    public async Task CopyToEndAsync(Stream destination, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(destination);
        while (_end > _start || await FillAsync(cancellation))
        {
            await destination.WriteAsync(_buffer.AsMemory(_start, _end - _start), cancellation);
            await destination.FlushAsync(cancellation);
            _start = _end;
        }
    }

    /// <summary>块长行的块长：1–16 位十六进制，之后只许空白再接分号开头的扩展（扩展字符只许 HTAB 与可见 ASCII 加空格）。</summary>
    public static long ChunkSize(ReadOnlySpan<byte> line)
    {
        var body = line[..^2];
        var digits = 0; long size = 0;
        while (digits < body.Length && Hex(body[digits]) >= 0)
        {
            if (++digits > 16 || size > long.MaxValue >> 4) throw new Http1Rejection(502, "chunk-size");
            size = (size << 4) | (uint)Hex(body[digits - 1]);
        }
        if (digits == 0) throw new Http1Rejection(502, "chunk-size");
        var rest = body[digits..];
        var semicolon = rest.TrimStart(" \t"u8);
        if (rest.Length > 0 && (semicolon.Length == 0 || semicolon[0] != (byte)';')) throw new Http1Rejection(502, "chunk-size");
        foreach (var value in rest) if (value != (byte)'\t' && value is < 0x20 or > 0x7E) throw new Http1Rejection(502, "chunk-extension");
        return size;
    }

    /// <summary>十六进制数字的值，不是返回 -1。</summary>
    private static int Hex(byte value) => value switch
    {
        >= (byte)'0' and <= (byte)'9' => value - '0',
        >= (byte)'a' and <= (byte)'f' => value - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => value - 'A' + 10,
        _ => -1,
    };

    /// <summary>读一行（含 CRLF），超过 <paramref name="max"/> 字节或出现裸 CR、裸 LF 拒绝，中途断开抛 <see cref="EndOfStreamException"/>。</summary>
    private async Task<byte[]> ReadLineAsync(int max, CancellationToken cancellation)
    {
        var scanned = 0;
        while (true)
        {
            for (var i = _start + scanned; i < _end; i++)
            {
                if (_buffer[i] == (byte)'\n')
                {
                    if (i == _start || _buffer[i - 1] != (byte)'\r') throw new Http1Rejection(502, "bare-line-ending");
                    var line = _buffer.AsSpan(_start, i + 1 - _start).ToArray();
                    _start = i + 1;
                    return line;
                }
                if (i > _start && _buffer[i - 1] == (byte)'\r') throw new Http1Rejection(502, "bare-line-ending");
                if (i + 1 - _start > max) throw new Http1Rejection(502, "line-too-long");
            }
            scanned = _end - _start;
            if (!await FillAsync(cancellation)) throw new EndOfStreamException("行没读完连接就关闭了。");
        }
    }

    /// <summary>缓冲不满时从源流再读一次；先把未交出的字节挪到缓冲开头。对端关闭返回 false。</summary>
    private async Task<bool> FillAsync(CancellationToken cancellation)
    {
        if (_start > 0)
        {
            _buffer.AsSpan(_start, _end - _start).CopyTo(_buffer);
            _end -= _start; _start = 0;
        }
        if (_end == _buffer.Length) throw new Http1Rejection(431, "head-too-large");
        var read = await _source.ReadAsync(_buffer.AsMemory(_end), cancellation);
        if (read == 0) return false;
        _end += read;
        return true;
    }
}
