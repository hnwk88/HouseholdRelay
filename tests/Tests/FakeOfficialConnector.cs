using Application.Interfaces.Siyu;
using Core.Siyu;

namespace Tests;

/// <summary>假的出站连接器：每次打开都给一条新的 <see cref="ScriptedStream"/>，回应字节由 <see cref="Replies"/> 决定；打开过的连接都留着供比对。</summary>
public sealed class FakeOfficialConnector : IOfficialConnector
{
    /// <summary>下一次打开时这条连接要回的全部字节（可以含多个回应，供同一连接上的多个请求用）。</summary>
    public Func<OfficialHost, byte[]> Replies { get; set; } = _ => [];

    /// <summary>按打开顺序记下的主机与连接。</summary>
    public List<(OfficialHost Host, ScriptedStream Stream)> Opened { get; } = [];

    /// <summary>给一条新的假连接。</summary>
    public Task<Stream> OpenAsync(OfficialHost host, CancellationToken cancellation)
    {
        var stream = new ScriptedStream(Replies(host));
        // 并发连接可能同时打开，记录表加锁。
        lock (Opened) Opened.Add((host, stream));
        return Task.FromResult<Stream>(stream);
    }
}
