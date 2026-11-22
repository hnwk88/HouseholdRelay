namespace Core.Siyu;

/// <summary>思域放行的每个官方路径各自的用途；准入、用量和错误归属都按它分。</summary>
public enum SiyuPurpose
{
    /// <summary>POST /v1/messages：推理。</summary>
    Messages,

    /// <summary>POST /v1/messages/count_tokens：估算。</summary>
    CountTokens,

    /// <summary>GET /v1/models：模型目录。</summary>
    Models,

    /// <summary>GET /api/claude_code/settings：账户附带的远程设置。</summary>
    Settings,

    /// <summary>GET /api/claude_code/policy_limits：账户附带的限额策略。</summary>
    PolicyLimits,

    /// <summary>GET /v1/mcp_servers：官方连接器发现。</summary>
    McpServers,

    /// <summary>mcp-proxy 上的 /v1/mcp/{连接器编号}：官方连接器传输。</summary>
    McpConnector,
}
