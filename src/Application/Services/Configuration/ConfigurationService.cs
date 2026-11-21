using Application.DependencyInjection;
using Application.Interfaces.Certificates;
using Application.Interfaces.Configuration;
using Application.Interfaces.Security;
using Application.Interfaces.Upstream;
using Core.Certificates;
using Core.Configuration;
using Core.Upstream;
using Microsoft.Extensions.DependencyInjection;

namespace Application.Services.Configuration;

/// <summary>管理配置的唯一写入路径；设置版本冲突须由操作者重新加载后决定。数据经通用仓储存取，每次操作一个作用域。</summary>
public sealed class ConfigurationService(IServiceScopeFactory scopes, ISecretProtector protector, IUpstreamService upstreams, ICertificateService certificates) : IConfigurationService
{
    /// <summary>读取并校验当前实例设置及其版本号，版本号用于保存时的乐观并发校验。</summary>
    public async Task<SettingsSnapshot> Read()
    {
        await using var scope = scopes.CreateAsyncScope();
        return (await scope.Repository<RelaySettings>().LoadSettings()).Snapshot();
    }

    /// <summary>
    /// 校验后按版本号保存设置：先做字段范围校验，再要求对外地址为空或为不含账号、查询、片段和路径的 HTTPS 根地址；
    /// 已导入证书时还要求对外域名与证书主机一致（不区分大小写），避免改了域名却仍用旧证书。不合法时抛出 <see cref="InvalidOperationException"/>；
    /// 版本冲突与设备编号被改由 <see cref="RelaySettings.Replace"/> 在写入的同一事务里拒绝。
    /// </summary>
    public async Task Save(long version, RelayConfiguration config)
    {
        config.Validate();
        if (config.PublicUrl.Length != 0 && !PublicAddress.IsHttpsRoot(config.PublicUrl, out _))
            throw new InvalidOperationException("对外地址应为 HTTPS 根地址，不含密码、查询参数或路径。");
        var certificate = await certificates.ReadInfo();
        if (certificate is not null && (config.PublicUrl.Length == 0 || !certificate.Host.Equals(new Uri(config.PublicUrl).DnsSafeHost, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("对外域名已变化，请在 HTTPS 入口页同时导入匹配的新证书。");
        await using var scope = scopes.CreateAsyncScope();
        var settings = scope.Repository<RelaySettings>();
        await settings.ExecuteInTransactionAsync(async () =>
        {
            (await settings.LoadSettings()).Replace(version, config);
            return await settings.SaveChangesAsync();
        });
    }

    /// <summary>活动上游是否已保存加密凭据；只检查是否存在，不解密。</summary>
    public async Task<bool> HasCredential()
    {
        await using var scope = scopes.CreateAsyncScope();
        return (await scope.Repository<ActiveUpstream>().GetByIdAsync(ActiveUpstream.SingletonId))?.Credential is not null;
    }

    /// <summary>是否已保存加密的服务器证书；只检查是否存在，不解密。</summary>
    public async Task<bool> HasCertificate()
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.Repository<ProtectedSecret>().AnyAsync(item => item.Name == ProtectedSecret.ServerCertificate);
    }

    /// <summary>解密并返回服务器证书 PFX 字节；未保存时返回 null。返回的是明文私钥材料，调用方用完后必须自行清零。</summary>
    public async Task<byte[]?> LoadCertificate()
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.Repository<ProtectedSecret>().FirstOrDefaultAsync(item => item.Name == ProtectedSecret.ServerCertificate) is { } secret
            ? protector.Unprotect(secret.Protected) : null;
    }

    /// <summary>以当前活动版本的配置加上新凭据保存为待测试候选，不直接替换活动上游；须经检测通过后才能激活。当前是官方登录上游时，新候选改为贴入的账户令牌。</summary>
    public async Task SaveCredential(string credential)
    {
        var active = await upstreams.ReadOverview();
        await upstreams.SaveCandidate(active.Version, active.Options with { OfficialLogin = false }, credential, active.Name);
    }

    /// <summary>
    /// 命令行初始化用：校验主机名（域名或 IPv4，不接受含冒号的 IPv6）、对外端口 1–65535、监听端口 1024–65535，
    /// 然后以 <c>https://主机:端口</c> 为对外地址创建私有 CA 与服务器证书。不合法时抛出 <see cref="InvalidOperationException"/>。
    /// </summary>
    public async Task ConfigureLegacyTls(string host, int publicPort, int listenPort)
    {
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown || host.Contains(':') || publicPort is < 1 or > 65535 || listenPort is < 1024 or > 65535)
            throw new InvalidOperationException("请输入有效域名或 IPv4 地址，端口必须有效。");
        await certificates.CreatePrivate((await Read()).Version, $"https://{host}:{publicPort}", listenPort);
    }
}
