namespace Infrastructure.ExternalSources.Siyu;

/// <summary>只写的分流：先把字节原样写给 <paramref name="primary"/>（客户端），写成了再交给用量旁路观察；旁路不改字节、也不抛。不拥有两端，释放什么也不做。</summary>
public sealed class TeeStream(Stream primary, SiyuUsageTap tap) : Stream
{
    /// <summary>不可读。</summary>
    public override bool CanRead => false;
    /// <summary>不可定位。</summary>
    public override bool CanSeek => false;
    /// <summary>可写。</summary>
    public override bool CanWrite => true;
    /// <summary>不支持。</summary>
    public override long Length => throw new NotSupportedException();
    /// <summary>不支持。</summary>
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    /// <summary>冲刷客户端一侧。</summary>
    public override void Flush() => primary.Flush();

    /// <summary>冲刷客户端一侧。</summary>
    public override Task FlushAsync(CancellationToken cancellationToken) => primary.FlushAsync(cancellationToken);

    /// <summary>写给客户端，再交给旁路。</summary>
    public override void Write(byte[] buffer, int offset, int count)
    {
        primary.Write(buffer, offset, count);
        tap.Observe(buffer.AsSpan(offset, count));
    }

    /// <summary>写给客户端，再交给旁路。</summary>
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await primary.WriteAsync(buffer, cancellationToken);
        tap.Observe(buffer.Span);
    }

    /// <summary>写给客户端，再交给旁路。</summary>
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <summary>不支持。</summary>
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    /// <summary>不支持。</summary>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    /// <summary>不支持。</summary>
    public override void SetLength(long value) => throw new NotSupportedException();
}
