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
using Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Repositories;

namespace Infrastructure.DependencyInjection;

/// <summary>基础设施层的依赖注入登记：SQLite 数据库与迁移链、EF Core 上下文与通用仓储（按作用域），DPAPI、证书、上游 HTTP 连接、Claude Code 进程等外部实现（按接口登记为单例）。</summary>
public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>登记数据目录选项、数据库及启动迁移、各仓储、安全组件、上游诊断、客户端启动器与上游连接池。</summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, Action<RelayDataOptions> configure)
    {
        var options = new RelayDataOptions();
        configure(options);
        services.AddSingleton(options);

        // 数据库构造时依次执行这些步骤；登记顺序与 Order 一致：旧数据导入、上游架构、用量架构、上游命名与全局代理、上游尝试记录、自动切换与按上游计价、上游导出。
        // 负责把旧版本数据目录里的配置文件导入新库（保留原文件与导入前快照）；选 Singleton 的原因：迁移无状态，只在打开数据库时按顺序执行一次。
        services.AddSingleton<IDatabaseMigration, LegacyImport>();
        // 负责把基础库升级到候选测试与版本化激活结构；选 Singleton 的原因：迁移无状态，只在打开数据库时按顺序执行一次。
        services.AddSingleton<IDatabaseMigration, UpstreamSchema>();
        // 负责建立逐请求用量表与索引；选 Singleton 的原因：迁移无状态，只在打开数据库时按顺序执行一次。
        services.AddSingleton<IDatabaseMigration, UsageSchema>();
        // 负责把库从版本 3 升到 4（供应商名称、活动上游来源主键、全局代理）；选 Singleton 的原因：迁移无状态，只在打开数据库时按顺序执行一次。
        services.AddSingleton<IDatabaseMigration, UpstreamNamingSchema>();
        // 负责把库从版本 4 升到 5（每次上游尝试的记录表）；选 Singleton 的原因：迁移无状态，只在打开数据库时按顺序执行一次。
        services.AddSingleton<IDatabaseMigration, UsageAttemptSchema>();
        // 负责把库从版本 5 升到 6（自动切换开关、用量记实际服务那一家、单价按已保存上游主键）；选 Singleton 的原因：迁移无状态，只在打开数据库时按顺序执行一次。
        services.AddSingleton<IDatabaseMigration, UpstreamFailoverSchema>();
        // 负责把库从版本 6 升到 7（已保存上游的导出时刻）；选 Singleton 的原因：迁移无状态，只在打开数据库时按顺序执行一次。
        services.AddSingleton<IDatabaseMigration, UpstreamExportSchema>();
        // 负责把库从版本 7 升到 8（站点身份代次与维护幂等历史）；选 Singleton 的原因：迁移无状态，只在打开数据库时按顺序执行一次。
        services.AddSingleton<IDatabaseMigration, SiteIdentitySchema>();
        // 负责把库从版本 8 升到 9（上游停用、三张用量相关表补外键、迁移另存）；选 Singleton 的原因：迁移无状态，只在打开数据库时按顺序执行一次。
        services.AddSingleton<IDatabaseMigration, UpstreamReferenceSchema>();
        // 负责把库从版本 9 升到 10（七张实体表主键改成库生成的整数、外键随之重建）；选 Singleton 的原因：迁移无状态，只在打开数据库时按顺序执行一次。
        services.AddSingleton<IDatabaseMigration, IntegerKeySchema>();
        // 负责把库从版本 10 升到 11（每张表补齐通用实体基类的列，复合主键与按名字存的表改整数主键）；选 Singleton 的原因：迁移无状态，只在打开数据库时按顺序执行一次。
        services.AddSingleton<IDatabaseMigration, EntityBaseSchema>();
        // 提供站点设备编号的唯一生成入口（D-012）；选 Singleton 的原因：无状态，全进程共用一个入口。
        services.AddSingleton<IDeviceIdGenerator, RandomDeviceIdGenerator>();
        services.AddSingleton<RelayDatabase>();
        services.AddSingleton<IRelayInstance>(provider => provider.GetRequiredService<RelayDatabase>());
        // 负责以独占打开 serving.lock 保证同一数据目录只有一个服务进程；选 Singleton 的原因：锁在进程生命周期内只持有一份。
        services.AddSingleton<IServingLock, ServingLockFile>();

        // 运行时所有数据经通用仓储走 EF Core：上下文按作用域，一次操作一个工作单元；选项先解析单例 RelayDatabase，它的构造跑完迁移链，所以第一个上下文用库之前库已是当前版本。
        services.AddDbContext<RelayDbContext>((provider, options) =>
        {
            var database = provider.GetRequiredService<RelayDatabase>();
            options.UseSqlite(database.ConnectionString).AddInterceptors(new RelayConnectionInterceptor(database),
                new RelayTransactionInterceptor(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(database.ConnectionString).DefaultTimeout));
        });
        services.AddRepositories<RelayDbContext>();
        // 提供「库正忙」的判断（怎么认是 SQLite 的细节，应用层经这个接口问）；选 Singleton 的原因：无状态。
        services.AddSingleton<IStorageBusy, SqliteStorageBusy>();
        // 维护本程序起过的 Claude 进程登记（只存操作号、PID、启动时刻）；选 Singleton 的原因：登记文件全进程共享一份，避免多份实例互相覆盖。
        services.AddSingleton<IOwnedClaudeProcesses, OwnedClaudeProcesses>();
        // 负责更新工程私有 Claude 配置里的 userID 与 oauthAccount；选 Singleton 的原因：无状态，每次调用都从文件读写。
        services.AddSingleton<IClaudeConfigurationPreparation, ClaudeConfigurationPreparation>();

        // 提供 DPAPI 机密保护与目录 ACL 收紧；选 Singleton 的原因：无状态。
        services.AddSingleton<ISecretProtector, DpapiSecretProtector>();
        // 提供上游导出文件的密码加密信封；选 Singleton 的原因：无状态，线程安全。
        services.AddSingleton<IPasswordCipher, PasswordFileCipher>();
        // 负责签发本实例私有 CA 与服务器证书；选 Singleton 的原因：无状态。
        services.AddSingleton<ICertificateFactory, CertificateFactory>();
        // 维护本实例的证书与机密存取；选 Singleton 的原因：无状态，数据每次经作用域内的仓储读写。
        services.AddSingleton<ICertificateService, CertificateService>();
        // 提供无秘密的客户端信任包；选 Singleton 的原因：无状态。
        services.AddSingleton<IClientTrustPackage, ClientTrustPackage>();
        // 执行管理密码的 PBKDF2 哈希与校验；选 Singleton 的原因：无状态，线程安全。
        services.AddSingleton<IAdministratorPasswordHasher, AdministratorPasswordHasher>();

        // 代理每次解析都重读设置库，项目设置里改完代理，新建立的上游连接立即按新方式走。
        // 配置服务经上游服务与探测间接依赖代理，所以到真要取代理时才解析它，构造时不成环。
        // IWebProxy 的取代理是同步合同，只能在这里同步等待一次读设置；Microsoft.Data.Sqlite 的异步读在内部同步完成，没有同步上下文，不会死锁。
        services.AddSingleton<IUpstreamProxy>(provider =>
            new SystemUpstreamProxy(() => provider.GetRequiredService<IConfigurationService>().Read().GetAwaiter().GetResult().Value));
        // 负责定位并启动本机原生 Claude Code；选 Singleton 的原因：无状态。
        services.AddSingleton<IClientLauncher, ClientLauncher>();
        // 执行隔离的原生客户端诊断；选 Singleton 的原因：无状态，每次诊断各起各的子进程。
        services.AddSingleton<IClaudeCodeProbe, ClaudeCodeProbe>();
        // 维护发往官方账户的统一身份缓存；选 Singleton 的原因：缓存与「只起一个子进程」的并发保护必须全进程共享。
        services.AddSingleton<IClaudeCodeIdentitySource, ClaudeCodeIdentitySource>();
        // 负责让本机 Claude Code 跟上客户端版本；选 Singleton 的原因：保证同一时刻只跑一次升级，失败间隔全进程共享。
        services.AddSingleton<IClaudeCodeUpdater, ClaudeCodeUpdater>();
        services.AddSingleton(new OfficialLoginOptions());
        // 负责官方账户的登录、导出与目录管理；选 Singleton 的原因：登录进行中的状态全进程共享一份。
        services.AddSingleton<IOfficialAccountLogin, OfficialAccountLogin>();
        // 执行已保存上游的独立诊断；选 Singleton 的原因：无状态。
        services.AddSingleton<IUpstreamProbe, UpstreamProbe>();
        return services.AddUpstreamConnections();
    }

    /// <summary>只登记上游连接池；独立中转模式（不连数据库）也用它，活动上游来源与启动配置由调用方登记，此时代理按启动配置解析。</summary>
    public static IServiceCollection AddUpstreamConnections(this IServiceCollection services)
    {
        services.TryAddSingleton<IUpstreamProxy>(provider => new SystemUpstreamProxy(provider.GetRequiredService<RelayConfiguration>));
        // 暂避计时用它；正式服务由应用层登记过，独立中转模式在这里补上系统时钟。
        services.TryAddSingleton(TimeProvider.System);
        // 维护到各上游的连接与暂避计时；选 Singleton 的原因：连接与暂避状态必须全进程共享，按请求新建会丢掉连接复用。
        services.AddSingleton<IUpstreamPool, UpstreamPool>();
        return services;
    }
}
