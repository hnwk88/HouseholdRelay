namespace Core.Siyu;

/// <summary>思域代理只放行的两个官方主机（端口固定 443）。名字的唯一来源是 <see cref="OfficialTargets"/>。</summary>
public enum OfficialHost
{
    /// <summary><c>api.anthropic.com</c>：推理、账户附带接口与连接器发现。</summary>
    Api,

    /// <summary><c>mcp-proxy.anthropic.com</c>：同账户发现得到的官方连接器。</summary>
    McpProxy,
}
