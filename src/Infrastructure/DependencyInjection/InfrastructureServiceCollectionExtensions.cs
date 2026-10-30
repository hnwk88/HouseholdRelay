using Application.Interfaces.Administration;
using Application.Interfaces.Certificates;
using Application.Interfaces.ClaudeCode;
using Application.Interfaces.Configuration;
using Application.Interfaces.Identity;
using Application.Interfaces.Security;
using Application.Interfaces.Upstream;
using Application.Interfaces.Usage;
using Core.Configuration;
using Infrastructure.ExternalSources.ClaudeCode;
using Infrastructure.ExternalSources.Network;
using Infrastructure.ExternalSources.Upstream;
using Infrastructure.Persistence;
using Infrastructure.Repositories.Administration;
using Infrastructure.Repositories.Certificates;
using Infrastructure.Repositories.Configuration;
using Infrastructure.Repositories.Identity;
using Infrastructure.Repositories.Upstream;
using Infrastructure.Repositories.Usage;
using Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Infrastructure.DependencyInjection;

/// <summary>基础设施层的依赖注入登记：SQLite 数据库与仓储、DPAPI、证书、上游 HTTP 连接、Claude Code 进程等外部实现，全部按接口登记为单例。</summary>
public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>登记数据目录选项、数据库及启动迁移、各仓储、安全组件、上游诊断、客户端启动器与上游连接池。</summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, Action<RelayDataOptions> configure)
    {
        var options = new RelayDataOptions();
        configure(options);
        services.AddSingleton(options);

        // 数据库构造时依次执行这些步骤；登记顺序与 Order 一致：旧数据导入、上游架构、用量架构、上游命名与全局代理。
        services.AddSingleton<IDatabaseMigration, LegacyImport>();
        services.AddSingleton<IDatabaseMigration, UpstreamSchema>();
        services.AddSingleton<IDatabaseMigration, UsageSchema>();
        services.AddSingleton<IDatabaseMigration, UpstreamNamingSchema>();
        services.AddSingleton<IDatabaseMigration, UsageAttemptSchema>();
        services.AddSingleton<RelayDatabase>();
        services.AddSingleton<IRelayInstance>(provider => provider.GetRequiredService<RelayDatabase>());
        services.AddSingleton<IServingLock, ServingLockFile>();

        services.AddSingleton<IConfigurationRepository, ConfigurationRepository>();
        services.AddSingleton<IIdentityRepository, IdentityRepository>();
        services.AddSingleton<IAdministratorRepository, AdministratorRepository>();
        services.AddSingleton<IUpstreamRepository, UpstreamRepository>();
        services.AddSingleton<ICertificateRepository, CertificateRepository>();
        services.AddSingleton<IUsageRepository, UsageRepository>();
        services.AddSingleton<IUsagePriceRepository, UsagePriceRepository>();

        services.AddSingleton<ISecretProtector, DpapiSecretProtector>();
        services.AddSingleton<ICertificateFactory, CertificateFactory>();
        services.AddSingleton<ICertificateService, CertificateService>();
        services.AddSingleton<IClientTrustPackage, ClientTrustPackage>();
        services.AddSingleton<IAdministratorPasswordHasher, AdministratorPasswordHasher>();

        // 代理每次解析都重读设置库，项目设置里改完代理，新建立的上游连接立即按新方式走。
        services.AddSingleton<IUpstreamProxy>(provider =>
        {
            var settings = provider.GetRequiredService<IConfigurationRepository>();
            return new SystemUpstreamProxy(() => settings.Read().Value);
        });
        services.AddSingleton<IClientLauncher, ClientLauncher>();
        services.AddSingleton<IClaudeCodeProbe, ClaudeCodeProbe>();
        services.AddSingleton(new OfficialLoginOptions());
        services.AddSingleton<IOfficialAccountLogin, OfficialAccountLogin>();
        services.AddSingleton<IUpstreamProbe, UpstreamProbe>();
        return services.AddUpstreamConnections();
    }

    /// <summary>只登记上游连接池；独立中转模式（不连数据库）也用它，活动上游来源与启动配置由调用方登记，此时代理按启动配置解析。</summary>
    public static IServiceCollection AddUpstreamConnections(this IServiceCollection services)
    {
        services.TryAddSingleton<IUpstreamProxy>(provider => new SystemUpstreamProxy(provider.GetRequiredService<RelayConfiguration>));
        services.AddSingleton<IUpstreamPool, UpstreamPool>();
        return services;
    }
}
