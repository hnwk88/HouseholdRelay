namespace Repositories;

/// <summary>
/// 定义仓储审计字段需要的当前用户或调用来源信息。
/// </summary>
/// <remarks>
/// 该接口只提供审计事实，不负责认证、授权或决定某条数据是否允许删除。
/// 删除权限、引用门禁和业务范围仍必须由应用服务在调用仓储前完成。
/// </remarks>
public interface ICurrentUserService
{
    /// <summary>
    /// 获取当前用户或调用来源的稳定标识。
    /// </summary>
    string? UserId { get; }

    /// <summary>
    /// 获取当前用户或调用来源的显示名称。
    /// </summary>
    string? UserName { get; }
}
