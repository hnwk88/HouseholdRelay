namespace Repositories;

/// <summary>
/// 提供通用仓储与宿主 DbContext 共同使用的命名查询过滤器标识。
/// </summary>
public static class RepositoryFilterNames
{
    /// <summary>
    /// 表示统一软删除过滤器；包含软删除数据时只能关闭该过滤器，不能同时关闭租户等安全过滤器。
    /// </summary>
    public const string SoftDeletion = "SoftDeletionFilter";

    /// <summary>
    /// 表示宿主工程的租户隔离过滤器；通用仓储不得主动关闭该过滤器。
    /// </summary>
    public const string Tenant = "TenantFilter";
}
