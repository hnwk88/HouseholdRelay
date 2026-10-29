using Application.Interfaces.Administration;
using Application.Interfaces.Configuration;
using Application.Interfaces.Identity;
using Application.Interfaces.Relay;
using Application.Interfaces.Upstream;
using Application.Interfaces.Usage;
using Application.Services.Administration;
using Application.Services.Configuration;
using Application.Services.Identity;
using Application.Services.Relay;
using Application.Services.Upstream;
using Application.Services.Usage;
using Core.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Application.DependencyInjection;

/// <summary>应用层的依赖注入登记：用例服务按接口登记为单例，与迁移前每个进程共用一份实例的语义一致。</summary>
public static class ApplicationServiceCollectionExtensions
{
    /// <summary>登记配置、使用者、管理员、上游、用量与费率服务，鉴权名单，默认时钟和默认（丢弃型）事件日志。</summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IRelayEventLog, NullRelayEventLog>();
        // 鉴权名单在创建时读一次已提交快照，之后每次鉴权前重读，使其他进程的撤销立即生效。
        services.AddSingleton(provider =>
        {
            var repository = provider.GetRequiredService<IIdentityRepository>();
            return new ClientRegistry(repository.ReadAccess(), repository.ReadAccess);
        });
        services.AddSingleton<IConfigurationService, ConfigurationService>();
        services.AddSingleton<IIdentityService, IdentityService>();
        services.AddSingleton<IAdministratorService, AdministratorService>();
        services.AddSingleton<IUpstreamService, UpstreamService>();
        services.AddSingleton<IActiveUpstreamSource>(provider => provider.GetRequiredService<IUpstreamService>());
        services.AddSingleton<IUsageService, UsageService>();
        services.AddSingleton<IUsagePricingService, UsagePricingService>();
        services.AddSingleton<UsageMaintenance>();
        return services;
    }

    /// <summary>登记只在正式服务宿主里运行的后台任务：启动恢复与每小时一次的 40 天用量清理。</summary>
    public static IServiceCollection AddApplicationBackgroundServices(this IServiceCollection services)
    {
        services.AddHostedService(provider => provider.GetRequiredService<UsageMaintenance>());
        return services;
    }
}
