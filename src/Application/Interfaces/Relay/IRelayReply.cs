namespace Application.Interfaces.Relay;

/// <summary>
/// 转发服务向客户端回话的唯一出口：设状态码、设响应头、写正文流、写 JSON、发本程序自带的静态文件、中止连接。
/// 不认识任何 HTTP 框架类型；宿主把自己的响应对象包成它交进来。
/// </summary>
public interface IRelayReply
{
    /// <summary>响应状态码；写出第一个字节前可改。</summary>
    int StatusCode { get; set; }

    /// <summary>是否已开始向客户端写出（状态行与响应头已发出）。</summary>
    bool HasStarted { get; }

    /// <summary>响应的 Content-Type。</summary>
    string? ContentType { get; set; }

    /// <summary>响应正文流；写完一段要立即送出时调用它的 FlushAsync。</summary>
    Stream Body { get; }

    /// <summary>设置一个响应头（同名覆盖）。</summary>
    void SetHeader(string name, string[] values);

    /// <summary>把 <paramref name="value"/> 序列化成 JSON 写出（application/json; charset=utf-8）。</summary>
    Task WriteJson(object value, CancellationToken cancellation);

    /// <summary>把程序目录里的一个文件原样写出。</summary>
    Task SendFile(string path, CancellationToken cancellation);

    /// <summary>中止连接，不再写任何东西。</summary>
    void Abort();
}
