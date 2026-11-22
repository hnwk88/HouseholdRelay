using System.Collections.Frozen;

namespace Core.Siyu;

/// <summary>
/// 一个官方账户在一个资料代次下、从官方 /v1/mcp_servers 成功响应里读出的连接器编号集合。
/// <see cref="StartedAt"/> 是那次发现开始的时刻，有效期从它算起（保守）；新旧集合按它比先后。
/// </summary>
public sealed record McpDiscoverySet(string ProfileVersion, FrozenSet<string> ConnectorIds, DateTimeOffset StartedAt, DateTimeOffset ExpiresAt);
