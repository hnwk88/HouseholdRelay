using Application.Interfaces.Relay;
using Microsoft.AspNetCore.Http;

namespace Infrastructure.ExternalSources.Upstream;

/// <summary>本层自己起的本机临时端点（Claude Code 诊断回环）把 ASP.NET Core 的响应包成回话出口，交给 <see cref="UpstreamResponses.Copy"/>；中转宿主有自己的一份。</summary>
internal sealed class HttpResponseReply(HttpContext context) : IRelayReply
{
    /// <summary>响应状态码，直接读写 ASP.NET Core 响应。</summary>
    public int StatusCode { get => context.Response.StatusCode; set => context.Response.StatusCode = value; }

    /// <summary>响应是否已开始写出。</summary>
    public bool HasStarted => context.Response.HasStarted;

    /// <summary>响应的 Content-Type。</summary>
    public string? ContentType { get => context.Response.ContentType; set => context.Response.ContentType = value; }

    /// <summary>响应正文流。</summary>
    public Stream Body => context.Response.Body;

    /// <summary>设置一个响应头，同名覆盖。</summary>
    public void SetHeader(string name, string[] values) => context.Response.Headers[name] = values;

    /// <summary>以 JSON 写出。</summary>
    public Task WriteJson(object value, CancellationToken cancellation) => context.Response.WriteAsJsonAsync(value, cancellation);

    /// <summary>原样写出一个文件。</summary>
    public Task SendFile(string path, CancellationToken cancellation) => context.Response.SendFileAsync(path, cancellation);

    /// <summary>中止连接。</summary>
    public void Abort() => context.Abort();
}
