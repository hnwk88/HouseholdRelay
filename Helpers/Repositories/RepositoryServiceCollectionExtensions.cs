using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Repositories;

/// <summary>
/// 提供通用仓储和数据库上下文的依赖注入注册入口。
/// </summary>
public static class RepositoryServiceCollectionExtensions
{
    /// <summary>
    /// 注册指定 EF Core 数据库上下文对应的通用仓储。
    /// </summary>
    /// <typeparam name="TDbContext">工程自己的数据库上下文类型。</typeparam>
    /// <param name="services">依赖注入服务集合。</param>
    /// <returns>原服务集合，便于继续链式注册。</returns>
    /// <remarks>
    /// 该方法不写死任何工程名称或上下文名称。工程应先调用 <c>AddDbContext&lt;TDbContext&gt;</c>，
    /// 再调用本方法；如果一个进程同时使用多个业务数据库上下文，应为每个上下文创建明确的适配仓储，
    /// 不要重复覆盖全局 <see cref="DbContext" /> 映射。
    /// </remarks>
    public static IServiceCollection AddRepositories<TDbContext>(this IServiceCollection services)
        where TDbContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<DbContext>(provider => provider.GetRequiredService<TDbContext>());
        services.TryAddSingleton<IDateTimeProvider, DateTimeProvider>();
        services.TryAddScoped<ICurrentUserService, NullCurrentUserService>();
        services.TryAddScoped(typeof(IRepository<>), typeof(Repository<>));
        return services;
    }
}
