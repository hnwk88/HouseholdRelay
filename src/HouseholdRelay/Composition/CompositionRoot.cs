using Application.DependencyInjection;
using Application.Interfaces.Configuration;
using Application.Interfaces.Relay;
using Application.Interfaces.Upstream;
using Application.Services.Relay;
using Application.Services.Upstream;
using Core.Configuration;
using Core.Identity;
using Core.Relay;
using HouseholdRelay.Commands;
using HouseholdRelay.Relay;
using Infrastructure.DependencyInjection;
using Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HouseholdRelay.Composition;

/// <summary>主程序组合根：只调用各层的登记扩展方法，再登记本层的中转端点与命令；全部依赖由容器构造。</summary>
public static class CompositionRoot
{
    /// <summary>完整登记：应用层、基础设施层（数据目录由 <paramref name="configureData"/> 指定）、启动配置快照、中转端点和命令。</summary>
    public static IServiceCollection AddHouseholdRelay(this IServiceCollection services, Action<RelayDataOptions> configureData)
    {
        services.AddApplication();
        services.AddInfrastructure(configureData);
        // 启动时读一次设置作为本进程的中转配置；监听端口和转发限制改动在重启服务后生效。
        services.AddSingleton(provider => provider.GetRequiredService<IConfigurationService>().Read().Value);
        services.AddRelayServer();
        services.AddSingleton<ManagementCommands>();
        services.AddSingleton<ServeCommand>();
        return services;
    }

    /// <summary>登记中转端点及其准入控制；启动配置、鉴权名单、上游连接池和事件日志由其他登记提供。</summary>
    public static IServiceCollection AddRelayServer(this IServiceCollection services)
    {
        services.AddSingleton<Admission>();
        services.AddSingleton<RelayServer>();
        return services;
    }

    /// <summary>不连数据库的独立中转：固定配置、固定凭据与可选的本机模拟上游，不记录用量。</summary>
    public static IServiceCollection AddStandaloneRelay(this IServiceCollection services, RelayConfiguration config, string? credential,
        Uri? testUpstream = null, ClientRegistry? clients = null)
    {
        services.AddSingleton(config);
        services.AddSingleton(clients ?? new ClientRegistry(config.Clients));
        services.AddSingleton<IActiveUpstreamSource>(FixedUpstreamSource.ForLegacy(config, credential, testUpstream));
        services.TryAddSingleton<IRelayEventLog, NullRelayEventLog>();
        services.AddUpstreamConnections();
        return services.AddRelayServer();
    }

    /// <summary>命令行子命令（init、account、clients、client）使用的容器；构建时校验全部登记可解析。</summary>
    public static ServiceProvider BuildCommandServices(string home) =>
        new ServiceCollection().AddHouseholdRelay(options => options.Home = home).BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
}
