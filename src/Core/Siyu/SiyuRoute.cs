namespace Core.Siyu;

/// <summary>一次内层请求匹配到的路由：用途，以及连接器路径里的编号（只有 <see cref="SiyuPurpose.McpConnector"/> 才有）。编号只是协议数据，放不放行另由发现集合决定。</summary>
public readonly record struct SiyuRoute(SiyuPurpose Purpose, string? ConnectorId);
