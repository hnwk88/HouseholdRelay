using Microsoft.Extensions.DependencyInjection;
using Repositories;

namespace Application.DependencyInjection;

/// <summary>
/// 单例服务每次操作开一个作用域，在作用域里按实体类型取通用仓储；同一作用域里取到的仓储共用一个数据库上下文，一次 <c>SaveChangesAsync</c> 提交本次全部改动。
/// 作用域随操作结束释放，数据库上下文绝不跨请求持有。
/// </summary>
public static class RepositoryScopeExtensions
{
    /// <summary>取这个作用域里 <typeparamref name="TEntity"/> 的通用仓储。</summary>
    /// <typeparam name="TEntity">实体类型。</typeparam>
    /// <param name="scope">本次操作的作用域。</param>
    public static IRepository<TEntity> Repository<TEntity>(this AsyncServiceScope scope) where TEntity : BaseEntity =>
        scope.ServiceProvider.GetRequiredService<IRepository<TEntity>>();
}
