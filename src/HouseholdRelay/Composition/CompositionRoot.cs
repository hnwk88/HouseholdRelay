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
using Application.Logging;
using Infrastructure.DependencyInjection;
using Infrastructure.Logging;
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
        // 依赖注入的工厂是同步合同，只能在这里同步等待一次；Microsoft.Data.Sqlite 的异步读在内部同步完成，没有同步上下文，不会死锁。
        services.AddSingleton(provider => provider.GetRequiredService<IConfigurationService>().Read().GetAwaiter().GetResult().Value);
        // 提供思域代理对外公布的地址（选 Singleton 的原因：启动时定一次）：主机取对外地址，端口取思域代理端口（对外与监听同号），只在开了思域、真要建会话服务时才算。
        services.AddSingleton(provider => SiyuEndpoint(provider.GetRequiredService<RelayConfiguration>()));
        services.AddRelayServer();
        // 处理思域控制面请求、负责构建思域代理宿主；选 Singleton 的原因：都只转交给同一个会话服务，无状态。serve 经 Lazy 取，只有开了思域才构造（会话服务随之构造并订阅失效源）。
        // 记录思域代理在本进程是否起来；选 Singleton 的原因：serve 写、管理页读，全进程共享一份。
        services.AddSingleton<Siyu.SiyuHostState>();
        services.AddSingleton<Siyu.SiyuControl>();
        services.AddSingleton(provider => new Siyu.SiyuProxyHost(provider.GetRequiredService<RelayConfiguration>(),
            provider.GetRequiredService<Infrastructure.ExternalSources.Siyu.SiyuProxyConnectionHandler>(), provider.GetRequiredService<ILoggerFactory>()));
        services.AddSingleton(provider => new Lazy<Siyu.SiyuControl>(provider.GetRequiredService<Siyu.SiyuControl>));
        services.AddSingleton(provider => new Lazy<Application.Interfaces.Siyu.ISiyuSessionService>(provider.GetRequiredService<Application.Interfaces.Siyu.ISiyuSessionService>));
        services.AddSingleton(provider => new Lazy<Siyu.SiyuProxyHost>(provider.GetRequiredService<Siyu.SiyuProxyHost>));
        services.AddSingleton<ManagementCommands>();
        services.AddSingleton<ServeCommand>();
        return services;
    }

    /// <summary>思域代理入口对外公布的地址：https、对外地址的主机名、思域代理端口；对外地址没填或不是 HTTPS 根地址时拒绝并读出下一步。</summary>
    internal static Application.Contracts.Siyu.SiyuProxyEndpoint SiyuEndpoint(RelayConfiguration config)
    {
        if (!PublicAddress.IsHttpsRoot(config.PublicUrl, out var address))
            throw new InvalidOperationException("思域需要先在项目设置里填好对外 HTTPS 地址，代理入口用它的域名。");
        return new(new UriBuilder(Uri.UriSchemeHttps, address.DnsSafeHost, config.SiyuProxyPort).Uri);
    }

    /// <summary>正式服务的中转：转发服务的十二个依赖全部由容器交进来，漏登记任何一个在构建容器时就报；启动配置、鉴权名单、上游连接池和事件日志由其他登记提供。</summary>
    public static IServiceCollection AddRelayServer(this IServiceCollection services)
    {
        services.AddSingleton<IRelayForwarder, RelayForwarder>();
        return services.AddRelayHost();
    }

    /// <summary>两种模式共用的中转宿主：准入控制与 HTTP 绑定；管理页经 <see cref="IHttpsListener"/> 读对外 HTTPS 是否已启动。</summary>
    private static IServiceCollection AddRelayHost(this IServiceCollection services)
    {
        // 中转宿主共用所在容器的日志工厂；正式服务里基础设施已挂上文件日志，独立中转没有数据目录，这里只保证工厂在（重复登记不叠加）。
        services.AddLogging();
        services.AddSingleton<Admission>();
        services.AddSingleton<RelayServer>();
        services.AddSingleton<IHttpsListener>(provider => provider.GetRequiredService<RelayServer>());
        return services;
    }

    /// <summary>不连数据库的独立中转：固定配置、固定凭据与可选的本机模拟上游；转发服务走 <see cref="RelayForwarder.Standalone"/>，明确不记用量、不发往官方账户。</summary>
    public static IServiceCollection AddStandaloneRelay(this IServiceCollection services, RelayConfiguration config, string? credential,
        Uri? testUpstream = null, ClientRegistry? clients = null)
    {
        services.AddSingleton(config);
        services.AddSingleton(clients ?? new ClientRegistry(config.Clients));
        services.AddSingleton<IActiveUpstreamSource>(FixedUpstreamSource.ForLegacy(config, credential, testUpstream));
        services.TryAddSingleton<IRelayEventLog, NullRelayEventLog>();
        services.AddStandaloneUpstreamConnections();
        services.AddSingleton<IRelayForwarder>(provider => RelayForwarder.Standalone(provider.GetRequiredService<RelayConfiguration>(), provider.GetRequiredService<ClientRegistry>(),
            provider.GetRequiredService<Admission>(), provider.GetRequiredService<IUpstreamPool>(), provider.GetRequiredService<IRelayEventLog>(), provider.GetRequiredService<IUpstreamResponses>(),
            provider.GetRequiredService<ILogger<RelayForwarder>>()));
        return services.AddRelayHost();
    }

    /// <summary>正式服务的事件输出：每个中转请求一行，写服务窗口与数据目录 logs\events.log（限大小轮换，中文不转义，写不出去不抛）。</summary>
    public static IServiceCollection AddServiceEventLog(this IServiceCollection services) => services.AddSingleton<IRelayEventLog, ServiceRelayEventLog>();

    /// <summary>命令行兜底的日志：在数据目录 <paramref name="home"/> 的 logs\errors.log 记一行异常类型链与阶段；记日志本身写不出去也不抛出。</summary>
    public static void RecordUnexpected(string home, string stage, Exception error)
    {
        using var loggers = LoggerFactory.Create(logging => logging.AddRelayFileLog(home));
        RelayLog.Unexpected(loggers.CreateLogger("HouseholdRelay.Program"), error, stage);
    }

    /// <summary>命令行子命令（init、account、clients、client）使用的容器；构建时校验全部登记可解析，并校验单例不持有作用域服务。</summary>
    public static ServiceProvider BuildCommandServices(string home) =>
        new ServiceCollection().AddHouseholdRelay(options => options.Home = home).BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
}
