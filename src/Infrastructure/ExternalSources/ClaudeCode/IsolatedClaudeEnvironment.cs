using System.Diagnostics;
using Core.Upstream;

namespace Infrastructure.ExternalSources.ClaudeCode;

/// <summary>
/// 中转启动的 Claude Code 子进程（检测、官方账户登录与刷新）共用的隔离环境：不继承宿主的 Anthropic、Claude 与证书变量，
/// 配置目录只用中转指定的那个，绝不落到用户自己的 <c>%USERPROFILE%\.claude</c>。
/// </summary>
public static class IsolatedClaudeEnvironment
{
    /// <summary>
    /// 先删掉继承的 <c>ANTHROPIC_*</c>、<c>CLAUDE_*</c>、<c>NODE_EXTRA_CA_CERTS</c>、<c>SSL_CERT_FILE</c>、<c>NODE_TLS_REJECT_UNAUTHORIZED</c>（宿主若带着
    /// <c>CLAUDE_CODE_OAUTH_TOKEN</c>，空目录也会被当成已登录），代理变量交给 <see cref="ChildProxyEnvironment.Apply"/> 先清后写，
    /// 再把 <c>CLAUDE_CONFIG_DIR</c> 指向 <paramref name="configDirectory"/> 并关闭非必要流量。
    /// </summary>
    public static void Apply(ProcessStartInfo start, string configDirectory, ProxyRoute route)
    {
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("CLAUDE_", StringComparison.OrdinalIgnoreCase)
            || key is "NODE_EXTRA_CA_CERTS" or "SSL_CERT_FILE" or "NODE_TLS_REJECT_UNAUTHORIZED").ToArray()) start.Environment.Remove(key);
        ChildProxyEnvironment.Apply(start, route);
        start.Environment["CLAUDE_CONFIG_DIR"] = configDirectory; start.Environment["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";
    }
}
