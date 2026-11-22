namespace Tests;

/// <summary>每次读最多只给 <c>step</c> 个字节的只读流：专测定界代码在任意读边界上都对（真实 TLS 流一次读到多少字节不固定）。</summary>
public sealed class TrickleStream(byte[] data, int step = 1) : Stream
{
    private int _position;

    /// <summary>可读。</summary>
    public override bool CanRead => true;

    /// <summary>不可定位。</summary>
    public override bool CanSeek => false;

    /// <summary>不可写。</summary>
    public override bool CanWrite => false;

    /// <summary>全部数据的长度。</summary>
    public override long Length => data.Length;

    /// <summary>已经给出的字节数；不支持设置。</summary>
    public override long Position { get => _position; set => throw new NotSupportedException(); }

    /// <summary>只读流，冲刷什么也不做。</summary>
    public override void Flush() { }

    /// <summary>最多给出 <c>step</c> 个字节。</summary>
    public override int Read(byte[] buffer, int offset, int count)
    {
        var take = Math.Min(Math.Min(count, step), data.Length - _position);
        Array.Copy(data, _position, buffer, offset, take);
        _position += take;
        return take;
    }

    /// <summary>不支持定位。</summary>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <summary>不支持改长度。</summary>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <summary>只读流，不支持写。</summary>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
