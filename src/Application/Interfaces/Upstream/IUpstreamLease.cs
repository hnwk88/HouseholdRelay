using Core.Upstream;
using Microsoft.Extensions.Primitives;

namespace Application.Interfaces.Upstream;

/// <summary>某个上游版本连接的租约：构造上游请求、发送并观察认证失败与限流；释放租约后才回收连接。</summary>
public interface IUpstreamLease : IDisposable
{
    /// <summary>租约所属的活动版本快照。</summary>
    UpstreamSnapshot Snapshot { get; }

    /// <summary>该版本是否因认证失败而暂停。</summary>
    bool Paused { get; }

    /// <summary>剩余冷却秒数；未冷却时为 0。</summary>
    int CooldownSeconds { get; }

    /// <summary>未配置、已暂停或冷却中时抛出对应的中转失败。</summary>
    void CheckState();

    /// <summary>按允许的路由构造上游请求：去掉私有和逐跳头，附加部署凭据与必要的协议头。</summary>
    HttpRequestMessage Request(string route, string query, byte[] body, string method, string userAgent, IDictionary<string, StringValues>? headers = null);

    /// <summary>发送请求并拒绝任何跳转响应。</summary>
    Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken cancellation);

    /// <summary>根据响应状态记录认证暂停或限流冷却。</summary>
    void Observe(HttpResponseMessage response);
}
