namespace Application.Contracts.Siyu;

/// <summary>
/// 标准模式的接入：<see cref="BaseUrl"/> 是站点自己的标准 API 根地址，<see cref="Protocol"/> 是它说的协议，<see cref="Authentication"/> 说明认证用个人令牌。
/// 第三方上游的密钥只在中转机上，这里永远不出现任何上游凭据。
/// </summary>
public sealed record SiyuStandardAccess(string BaseUrl, string Protocol, string Authentication);
