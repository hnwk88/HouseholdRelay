namespace Tests;

/// <summary>假的官方出站连接：读出事先写好的回应字节，写入的字节全部记下来（用来比对发给官方的原始请求）。</summary>
public sealed class ScriptedStream(byte[] replies) : Stream
{
    private readonly MemoryStream _replies = new(replies);

    /// <summary>写进来的全部字节。</summary>
    public MemoryStream Written { get; } = new();

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

    /// <summary>冲刷什么也不做。</summary>
    public override void Flush() { }

    /// <summary>从事先写好的回应里读。</summary>
    public override int Read(byte[] buffer, int offset, int count) => _replies.Read(buffer, offset, count);

    /// <summary>记下写入的字节。</summary>
    public override void Write(byte[] buffer, int offset, int count) => Written.Write(buffer, offset, count);

    /// <summary>不支持定位。</summary>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <summary>不支持改长度。</summary>
    public override void SetLength(long value) => throw new NotSupportedException();
}
