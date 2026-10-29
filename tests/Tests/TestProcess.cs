using HouseholdRelay.Composition;
using Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace Tests;

/// <summary>测试用的组合根：每个实例相当于一个独立进程，用与主程序相同的登记方法组装各自的数据库与服务。</summary>
sealed class TestProcess
{
    // 不打开数据库的无状态组件（证书签发、客户端定位）由一个共享容器提供。
    private static readonly ServiceProvider Shared = new ServiceCollection().AddInfrastructure(options => options.Home = Path.GetTempPath()).BuildServiceProvider();
    /// <summary>证书签发与加载。</summary>
    public static ICertificateFactory CertificateFactory => Shared.GetRequiredService<ICertificateFactory>();
    /// <summary>本机 Claude Code 定位与启动。</summary>
    public static IClientLauncher ClientLauncher => Shared.GetRequiredService<IClientLauncher>();

    private readonly ServiceProvider provider;
    /// <summary>以数据目录、可选的固定时钟和可选的迁移注入点组装一个独立进程的容器。</summary>
    public TestProcess(string home, TimeProvider? clock = null, Action? migrationCheckpoint = null)
    {
        var services = new ServiceCollection().AddHouseholdRelay(options => { options.Home = home; options.MigrationCheckpoint = migrationCheckpoint; });
        if (clock is not null) services.AddSingleton<TimeProvider>(clock);
        provider = services.BuildServiceProvider();
    }
    /// <summary>从本进程容器取服务。</summary>
    public T Get<T>() where T : notnull => provider.GetRequiredService<T>();
    /// <summary>本进程的数据库；首次取用时打开并完成迁移。</summary>
    public RelayDatabase Database => Get<RelayDatabase>();
    /// <summary>配置服务。</summary>
    public IConfigurationService Settings => Get<IConfigurationService>();
    /// <summary>使用者服务。</summary>
    public IIdentityService Identities => Get<IIdentityService>();
    /// <summary>管理员服务。</summary>
    public IAdministratorService Administrators => Get<IAdministratorService>();
    /// <summary>上游服务。</summary>
    public IUpstreamService Upstreams => Get<IUpstreamService>();
    /// <summary>证书服务（具体类型，以便调用测试专用的内部重载）。</summary>
    public CertificateService Certificates => (CertificateService)Get<ICertificateService>();
    /// <summary>用量服务。</summary>
    public IUsageService Usage => Get<IUsageService>();
    /// <summary>费率服务。</summary>
    public IUsagePricingService Pricing => Get<IUsagePricingService>();
    /// <summary>直接解密活动行里的凭据（不经上游服务，库还没升级完时也能读）；未配置时为 null。</summary>
    public string? ActiveCredential() => Get<IUpstreamRepository>().ReadActiveCredential() is { } encrypted
        ? System.Text.Encoding.UTF8.GetString(Get<Application.Interfaces.Security.ISecretProtector>().Unprotect(encrypted)) : null;

    /// <summary>不连数据库的独立中转容器：固定凭据、可选模拟上游、可选鉴权名单、事件日志与代理解析器（给出时取代按启动配置解析的默认实现）。</summary>
    public static ServiceProvider StandaloneRelay(RelayConfiguration config, string? credential, Uri? testUpstream = null, ClientRegistry? clients = null, IRelayEventLog? log = null, IUpstreamProxy? proxy = null)
    {
        var services = new ServiceCollection();
        if (proxy is not null) services.AddSingleton(proxy);
        services.AddStandaloneRelay(config, credential, testUpstream, clients);
        if (log is not null) services.AddSingleton(log);
        return services.BuildServiceProvider();
    }
}

/// <summary>独立中转测试夹具的事件日志：把每条事件序列化后放进队列供断言检查。</summary>
sealed class FixtureLog(System.Collections.Concurrent.ConcurrentQueue<string> logs) : IRelayEventLog
{
    /// <summary>序列化事件并入队。</summary>
    public void Write(object entry) => logs.Enqueue(System.Text.Json.JsonSerializer.Serialize(entry));
}
