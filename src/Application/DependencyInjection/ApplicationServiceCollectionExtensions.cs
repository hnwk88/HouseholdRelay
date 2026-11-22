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
        // 负责限时限量记录协议诊断；选 Singleton 的原因：诊断开关与已记条数全进程共享一份。
        services.AddSingleton<IProtocolDiagnostics, ProtocolDiagnostics>();
        // 鉴权名单在创建时读一次已提交快照，之后每次鉴权前重读，使其他进程的撤销立即生效。
        // 依赖注入的工厂是同步合同，初值只能在这里同步等待一次；Microsoft.Data.Sqlite 的异步读在内部同步完成，没有同步上下文，不会死锁。
        services.AddSingleton(provider =>
        {
            var scopes = provider.GetRequiredService<IServiceScopeFactory>();
            return new ClientRegistry(IdentityService.ReadCommitted(scopes).GetAwaiter().GetResult(), () => IdentityService.ReadCommitted(scopes));
        });
        // 维护实例设置的读取与按版本保存；选 Singleton 的原因：数据每次经独立作用域的仓储读写，服务本身无状态，全进程共用。
        services.AddSingleton<IConfigurationService, ConfigurationService>();
        // 维护站点身份与设备编号的换号流程；选 Singleton 的原因：当前身份快照与换号串行锁必须全进程共享。
        services.AddSingleton<ISiteIdentityService, SiteIdentityService>();
        // 维护使用者与个人令牌；选 Singleton 的原因：写入串行锁与鉴权名单发布必须全进程共享一份。
        services.AddSingleton<IIdentityService, IdentityService>();
        // 维护管理员账户、首次设置与管理会话；选 Singleton 的原因：数据每次经独立作用域的仓储读写，服务本身无状态。
        services.AddSingleton<IAdministratorService, AdministratorService>();
        // 维护已保存上游、测试与激活；选 Singleton 的原因：测试与激活的状态都存在库里，服务本身无状态，每次经独立作用域的仓储读写。
        services.AddSingleton<IUpstreamService, UpstreamService>();
        services.AddSingleton<IActiveUpstreamSource>(provider => provider.GetRequiredService<IUpstreamService>());
        // 负责获取模型与测试的后台调度；选 Singleton 的原因：进行中的检测与最近的记录只在内存里，全进程共享一份，页面按编号查。
        services.AddSingleton<UpstreamChecks>();
        services.AddSingleton<IUpstreamChecks>(provider => provider.GetRequiredService<UpstreamChecks>());
        // 负责用量记录的写入、清理与查询；选 Singleton 的原因：写入队列与失败计数必须全进程共享。
        services.AddSingleton<IUsageService, UsageService>();
        // 维护按上游、按模型的单价；选 Singleton 的原因：数据每次经独立作用域的仓储读写，服务本身无状态。
        services.AddSingleton<IUsagePricingService, UsagePricingService>();
        services.AddSingleton<UsageMaintenance>();
        return services;
    }

    /// <summary>登记只在正式服务宿主里运行的后台任务：启动恢复与每小时一次的 40 天用量清理；后台检测（启动时把残留的「检测中」写成「检测未完成」，停止时取消进行中的检测）。</summary>
    public static IServiceCollection AddApplicationBackgroundServices(this IServiceCollection services)
    {
        services.AddHostedService(provider => provider.GetRequiredService<UsageMaintenance>());
        services.AddHostedService(provider => provider.GetRequiredService<UpstreamChecks>());
        return services;
    }
}
