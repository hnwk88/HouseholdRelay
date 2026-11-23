namespace Infrastructure.ExternalSources.Siyu;

/// <summary>
/// 把一个回应体按定界原样读进内存（线上字节，chunked 含分块框架），超过上限抛 <see cref="Http1Rejection"/> 502 <c>body-too-large</c>。
/// 只用于必须先看完再转发的少数回应（连接器发现），普通回应仍边到边转。
/// </summary>
public static class Http1BodyBuffer
{
    /// <summary>线上字节上限。</summary>
    public const int MaxWireBytes = 1024 * 1024;

    /// <summary>按 <paramref name="head"/> 的定界读全回应体。</summary>
    public static async Task<byte[]> ReadAsync(Http1Reader reader, Http1ResponseHead head, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(reader); ArgumentNullException.ThrowIfNull(head);
        var sink = new Bounded();
        switch (head.Framing)
        {
            case Http1BodyFraming.Length:
                if (head.ContentLength > MaxWireBytes) throw TooLarge();
                await reader.CopyExactAsync(head.ContentLength, sink, cancellation); break;
            case Http1BodyFraming.Chunked: await reader.CopyChunkedAsync(sink, cancellation); break;
            case Http1BodyFraming.UntilClose: await reader.CopyToEndAsync(sink, cancellation); break;
            case Http1BodyFraming.None: break;
        }
        return sink.ToArray();
    }

    /// <summary>超限拒绝。</summary>
    private static Http1Rejection TooLarge() => new(502, "body-too-large");

    /// <summary>写满上限就拒绝的内存流。</summary>
    private sealed class Bounded : MemoryStream
    {
        /// <summary>超限前写入。</summary>
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Length + count > MaxWireBytes) throw TooLarge();
            base.Write(buffer, offset, count);
        }

        /// <summary>超限前写入。</summary>
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Length + buffer.Length > MaxWireBytes) throw TooLarge();
            base.Write(buffer);
        }

        /// <summary>超限前写入。</summary>
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Length + buffer.Length > MaxWireBytes) throw TooLarge();
            return base.WriteAsync(buffer, cancellationToken);
        }

        /// <summary>超限前写入。</summary>
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (Length + count > MaxWireBytes) throw TooLarge();
            return base.WriteAsync(buffer, offset, count, cancellationToken);
        }
    }
}
