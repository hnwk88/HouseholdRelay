namespace Infrastructure.ExternalSources.Siyu;

/// <summary>把一读一写两个流合成一个双向流（Kestrel 连接的 Input、Output 管道各自转成流后合在一起），供 <see cref="System.Net.Security.SslStream"/> 和读取器使用。</summary>
public sealed class DuplexStream(Stream input, Stream output) : Stream
{
    /// <summary>可读。</summary>
    public override bool CanRead => true;

    /// <summary>不可定位。</summary>
    public override bool CanSeek => false;

    /// <summary>可写。</summary>
    public override bool CanWrite => true;

    /// <summary>不支持长度。</summary>
    public override long Length => throw new NotSupportedException();

    /// <summary>不支持位置。</summary>
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    /// <summary>冲刷写端。</summary>
    public override void Flush() => output.Flush();

    /// <summary>异步冲刷写端。</summary>
    public override Task FlushAsync(CancellationToken cancellationToken) => output.FlushAsync(cancellationToken);

    /// <summary>从读端读。</summary>
    public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);

    /// <summary>从读端异步读。</summary>
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => input.ReadAsync(buffer, cancellationToken);

    /// <summary>从读端异步读（数组重载）。</summary>
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => input.ReadAsync(buffer, offset, count, cancellationToken);

    /// <summary>写到写端。</summary>
    public override void Write(byte[] buffer, int offset, int count) => output.Write(buffer, offset, count);

    /// <summary>异步写到写端。</summary>
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => output.WriteAsync(buffer, cancellationToken);

    /// <summary>异步写到写端（数组重载）。</summary>
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => output.WriteAsync(buffer, offset, count, cancellationToken);

    /// <summary>不支持定位。</summary>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <summary>不支持改长度。</summary>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <summary>释放时两端一起释放。</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing) { input.Dispose(); output.Dispose(); }
        base.Dispose(disposing);
    }
}
