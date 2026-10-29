using System.Diagnostics;
using Core.Upstream;

namespace Infrastructure.ExternalSources.ClaudeCode;

/// <summary>
/// 子进程代理环境（照 MyCoAI <c>CliProxyPolicy</c> 移植）：子进程会继承宿主大小写两套代理变量，而 Claude Code 让小写优先，
/// 所以先清掉全部继承值，再按全局设置写入；启用代理却取不到地址的情况由 <see cref="Application.Interfaces.Upstream.IUpstreamProxy.Resolve"/> 在这之前拒绝。
/// </summary>
public static class ChildProxyEnvironment
{
    private static readonly string[] Names = ["HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY", "http_proxy", "https_proxy", "no_proxy"];

    /// <summary>先删除六个代理变量（大小写各三个）及其他名字含 PROXY 的继承变量，再按 <paramref name="route"/> 写入：有代理时大小写都写 HTTP(S)_PROXY，NO_PROXY 始终写入并含本机回环地址。</summary>
    public static void Apply(ProcessStartInfo start, ProxyRoute route)
    {
        foreach (var name in Names) start.Environment.Remove(name);
        foreach (var key in start.Environment.Keys.Where(key => key.Contains("PROXY", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        if (route.Address is { } address)
        {
            var value = address.AbsoluteUri.TrimEnd('/');
            start.Environment["HTTP_PROXY"] = value; start.Environment["http_proxy"] = value;
            start.Environment["HTTPS_PROXY"] = value; start.Environment["https_proxy"] = value;
        }
        start.Environment["NO_PROXY"] = route.NoProxy; start.Environment["no_proxy"] = route.NoProxy;
    }
}
