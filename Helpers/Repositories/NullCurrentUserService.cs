namespace Repositories;

/// <summary>
/// 表示没有可用用户上下文时的默认审计来源。
/// </summary>
/// <remarks>
/// 该实现返回空值，不伪造固定用户；部署工程可以用自己的认证适配器替换它。
/// </remarks>
public sealed class NullCurrentUserService : ICurrentUserService
{
    /// <summary>
    /// 获取空用户标识。
    /// </summary>
    public string? UserId => null;

    /// <summary>
    /// 获取空用户名称。
    /// </summary>
    public string? UserName => null;
}
