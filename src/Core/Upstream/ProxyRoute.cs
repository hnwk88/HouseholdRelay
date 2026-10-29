namespace Core.Upstream;

/// <summary>
/// 对某个目标地址解析出的出站代理：<paramref name="Address"/> 为 null 表示直连；<paramref name="NoProxy"/> 是写给子进程的 NO_PROXY 值，
/// 始终含本机回环地址；<paramref name="Description"/> 是读给管理员的来源说明，例如「自动检测到 http://127.0.0.1:7897」。
/// </summary>
public sealed record ProxyRoute(Uri? Address, string NoProxy, string Description);
