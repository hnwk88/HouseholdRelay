using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Application.DependencyInjection;
using Application.Interfaces.Certificates;
using Application.Interfaces.Security;
using Application.Services.Configuration;
using Core.Certificates;
using Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Repositories;

namespace Infrastructure.Security;

/// <summary>
/// 导入系统信任的服务器证书或在设置事务内签发私有证书，复用 DPAPI 机密；只向客户端提供公共信任材料。
/// 证书密文（secrets 的 tls）、展示信息与私有 CA 公共 PEM（public_assets 的 tls_info、legacy_ca）和设置经通用仓储存取，保存时与设置在同一个事务里。
/// </summary>
internal sealed class CertificateService(IServiceScopeFactory scopes, ISecretProtector protector, ICertificateFactory factory) : ICertificateService
{
    /// <summary>读取证书展示信息；迁移来的私有证书按当前对外地址补出展示信息。</summary>
    public async Task<CertificateInfo?> ReadInfo()
    {
        if (await Asset(PublicAsset.CertificateInfoName) is { } stored) return JsonSerializer.Deserialize<CertificateInfo>(stored, ConfigurationFiles.Json);
        // 迁移来的私有证书沿用原根与私钥，仅补充展示信息，不改变客户端信任。
        if (await ReadAuthorityPem() is null) return null;
        var pfx = await LoadCertificate();
        if (pfx is null) return null;
        try
        {
            using var leaf = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.EphemeralKeySet);
            return new("private", PublicAddress((await Settings()).PublicUrl).DnsSafeHost, leaf.NotAfter.ToUniversalTime(), leaf.Thumbprint, DateTimeOffset.MinValue);
        }
        // 解密出的 PFX 含服务器私钥，读完展示信息无论成败都立即清零。
        finally { CryptographicOperations.ZeroMemory(pfx); }
    }

    /// <summary>按系统信任和吊销检查导入 PFX。</summary>
    public Task Import(long settingsVersion, string publicUrl, int listenPort, byte[] pfx, string? password)
        => Import(settingsVersion, publicUrl, listenPort, pfx, password, null);

    /// <summary>导入 PFX 的实际实现：限制 1 MB 以内，校验对外地址与端口，要求恰好一个带私钥的服务器证书并通过链校验，再把整套证书导出、经 DPAPI 加密后与设置一起按 <paramref name="settingsVersion"/> 乐观并发保存。<paramref name="testRoots"/> 仅供测试，用自定义根代替系统信任。明文 PFX 和加载的证书对象在方法内清零或释放。</summary>
    internal async Task Import(long settingsVersion, string publicUrl, int listenPort, byte[] pfx, string? password, X509Certificate2Collection? testRoots)
    {
        if (pfx.Length is 0 or > 1024 * 1024) throw new InvalidOperationException("PFX 文件必须小于 1 MB，且包含服务器私钥和证书链。");
        var config = await Settings();
        config.PublicUrl = publicUrl.Trim(); config.ListenPort = listenPort; config.Validate();
        var url = PublicAddress(config.PublicUrl);
        X509Certificate2Collection certificates;
        try { certificates = X509CertificateLoader.LoadPkcs12Collection(pfx, password, X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable); }
        // 密码错误或文件损坏时换成面向管理员的提示，不把底层加密异常细节返回到网页。
        catch (CryptographicException) { throw new InvalidOperationException("无法读取 PFX，请检查文件和证书密码。"); }
        try
        {
            var certificate = certificates.Cast<X509Certificate2>().SingleOrDefault(item => item.HasPrivateKey)
                ?? throw new InvalidOperationException("PFX 必须恰好包含一个服务器私钥。");
            Validate(certificate, certificates, url.DnsSafeHost, testRoots);
            var info = new CertificateInfo("trusted", url.DnsSafeHost, certificate.NotAfter.ToUniversalTime(), certificate.Thumbprint, DateTimeOffset.UtcNow);
            var plain = certificates.Export(X509ContentType.Pkcs12) ?? throw new InvalidOperationException("证书导出失败。");
            try
            {
                var encrypted = protector.Protect(plain);
                await Save(settingsVersion, config, encrypted, info, null);
            }
            // 导出的明文 PFX 只用于加密，保存成功或失败都立即清零，库里只留 DPAPI 密文。
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        // 校验失败或保存失败时也要释放全部临时证书对象，避免私钥句柄滞留。
        finally { foreach (var certificate in certificates) certificate.Dispose(); }
    }

    /// <summary>解析并校验对外地址：必须是 HTTPS 根地址，不得含用户信息、查询、片段或路径；证书主机名都取自这里。</summary>
    internal static Uri PublicAddress(string value)
    {
        return Core.Configuration.PublicAddress.IsHttpsRoot(value, out var url) ? url : throw new InvalidOperationException("对外地址必须是 HTTPS 根地址，不含密码、查询参数或路径。");
    }

    /// <summary>校验服务器证书：必须含私钥、在有效期内、按 SAN 匹配主机名（允许通配符、不看 CN）、不是 CA 证书，并以服务器认证用途建链。未给 <paramref name="testRoots"/> 时按系统信任并在线检查吊销（超时 5 秒）；给出时只信任这些根、不查吊销也不下载证书，用于本实例私有 CA。不通过时抛 <see cref="InvalidOperationException"/>。</summary>
    internal static void Validate(X509Certificate2 certificate, X509Certificate2Collection intermediates, string host, X509Certificate2Collection? testRoots = null)
    {
        var now = DateTime.UtcNow;
        if (!certificate.HasPrivateKey || certificate.NotBefore.ToUniversalTime() > now || certificate.NotAfter.ToUniversalTime() <= now
            || !certificate.MatchesHostname(host, allowWildcards: true, allowCommonName: false))
            throw new InvalidOperationException("证书缺少私钥、域名不匹配或不在有效期内。");
        if (certificate.Extensions.OfType<X509BasicConstraintsExtension>().Any(extension => extension.CertificateAuthority))
            throw new InvalidOperationException("请导入服务器证书，不能使用 CA 证书作为服务器证书。");
        using var chain = new X509Chain();
        chain.ChainPolicy.ExtraStore.AddRange(intermediates);
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        chain.ChainPolicy.RevocationMode = X509RevocationMode.Online; chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(5);
        if (testRoots is not null)
        {
            // 私有证书使用本实例根；外部 PFX 导入的公共入口仍使用系统信任及吊销检查。
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust; chain.ChainPolicy.CustomTrustStore.AddRange(testRoots);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck; chain.ChainPolicy.DisableCertificateDownloads = true;
        }
        if (!chain.Build(certificate)) throw new InvalidOperationException("证书链不受系统信任、用途不符或吊销检查未通过，请补齐有效证书链。");
    }

    /// <summary>加载并重新校验当前证书及其链。</summary>
    public async Task<ServerCertificates?> LoadForServing()
    {
        var pfx = await LoadCertificate();
        if (pfx is null) return null;
        X509Certificate2Collection certificates;
        try { certificates = X509CertificateLoader.LoadPkcs12Collection(pfx, null, X509KeyStorageFlags.UserKeySet); }
        // 证书已加载进对象，解密出的 PFX 字节不再需要，加载成败都立即清零。
        finally { CryptographicOperations.ZeroMemory(pfx); }
        try
        {
            var certificate = certificates.Cast<X509Certificate2>().Single(item => item.HasPrivateKey);
            var host = PublicAddress((await Settings()).PublicUrl).DnsSafeHost;
            if ((await ReadInfo())?.Mode == "trusted") Validate(certificate, certificates, host);
            else
            {
                // 私有 CA 使用本实例确切的根验证，仍检查名称、有效期和服务器用途。
                var pem = await ReadAuthorityPem() ?? throw new InvalidOperationException("私有 CA 公共证书缺失，请恢复完整备份。");
                var authority = X509Certificate2.CreateFromPem(pem); certificates.Add(authority);
                Validate(certificate, certificates, host, new X509Certificate2Collection(authority));
            }
            return new ServerCertificates(certificate, certificates);
        }
        // 重新校验失败时释放已加载的证书再原样抛出，阻止用不合格证书提供服务；成功时证书所有权交给调用方。
        catch { foreach (var certificate in certificates) certificate.Dispose(); throw; }
    }

    /// <summary>私有 CA 公共 PEM。</summary>
    public Task<string?> ReadAuthorityPem() => Asset(PublicAsset.AuthorityName);

    /// <summary>私有 CA 的 SHA-256 指纹。</summary>
    public async Task<string?> AuthorityFingerprint()
    {
        var pem = await ReadAuthorityPem();
        if (pem is null) return null;
        using var authority = X509Certificate2.CreateFromPem(pem);
        return authority.GetCertHashString(HashAlgorithmName.SHA256);
    }

    /// <summary>签发新的私有 CA 与服务器证书并与设置同事务保存。</summary>
    public async Task CreatePrivate(long settingsVersion, string publicUrl, int listenPort, bool replaceExisting = false)
    {
        if (await ProtectedCertificate() is not null && !replaceExisting)
            throw new InvalidOperationException("已有证书。更换将生成新的信任根，须确认每台客户端重新安装信任。");
        var config = await Settings();
        config.PublicUrl = publicUrl.Trim(); config.ListenPort = listenPort; config.Validate();
        var address = PublicAddress(config.PublicUrl);
        using var leaf = factory.CreateCertificate(address.DnsSafeHost, out var pem);
        using var authority = X509Certificate2.CreateFromPem(pem);
        Validate(leaf, new X509Certificate2Collection(authority), address.DnsSafeHost, new X509Certificate2Collection(authority));
        var plain = leaf.Export(X509ContentType.Pkcs12);
        try
        {
            var encrypted = protector.Protect(plain);
            var info = new CertificateInfo("private", address.DnsSafeHost, leaf.NotAfter.ToUniversalTime(), leaf.Thumbprint, DateTimeOffset.UtcNow);
            await Save(settingsVersion, config, encrypted, info, pem);
        }
        // 新签发证书的明文 PFX 只用于加密，保存成败都立即清零。
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    /// <summary>读取库中受 DPAPI 保护的服务器证书并解密为 PFX 字节，没有证书时返回 null；返回的明文含私钥，调用方用完必须清零。</summary>
    private async Task<byte[]?> LoadCertificate() => await ProtectedCertificate() is byte[] encrypted ? protector.Unprotect(encrypted) : null;

    /// <summary>读取库中服务器证书的 DPAPI 密文；未配置时为空。</summary>
    private async Task<byte[]?> ProtectedCertificate()
    {
        await using var scope = scopes.CreateAsyncScope();
        return (await scope.Repository<ProtectedSecret>().FirstOrDefaultAsync(item => item.Name == ProtectedSecret.ServerCertificate))?.Protected;
    }

    /// <summary>按名字读取一段公共资产；没有时为空。</summary>
    private async Task<string?> Asset(string name)
    {
        await using var scope = scopes.CreateAsyncScope();
        return (await scope.Repository<PublicAsset>().FirstOrDefaultAsync(item => item.Name == name))?.Value;
    }

    /// <summary>读取并校验当前设置。</summary>
    private async Task<RelayConfiguration> Settings()
    {
        await using var scope = scopes.CreateAsyncScope();
        return (await scope.Repository<RelaySettings>().LoadSettings()).Snapshot().Value;
    }

    /// <summary>
    /// 在一个事务里保存证书：设置按 <paramref name="settingsVersion"/> 乐观并发写入（设备编号不许变），写入或覆盖证书密文与展示信息；
    /// 给了 <paramref name="authorityPem"/>（私有证书）就写入私有 CA 公共 PEM，没给（导入的可信证书）就物理删除旧私有 CA。
    /// </summary>
    private async Task Save(long settingsVersion, RelayConfiguration config, byte[] protectedPfx, CertificateInfo info, string? authorityPem)
    {
        await using var scope = scopes.CreateAsyncScope();
        var settings = scope.Repository<RelaySettings>(); var secrets = scope.Repository<ProtectedSecret>(); var assets = scope.Repository<PublicAsset>();
        await settings.ExecuteInTransactionAsync(async () =>
        {
            (await settings.LoadSettings()).Replace(settingsVersion, config);
            var secret = await secrets.FirstOrDefaultAsync(item => item.Name == ProtectedSecret.ServerCertificate);
            if (secret is null) await secrets.AddAsync(secret = new ProtectedSecret { Name = ProtectedSecret.ServerCertificate });
            secret.Protected = protectedPfx;
            await Put(assets, PublicAsset.CertificateInfoName, JsonSerializer.Serialize(info, ConfigurationFiles.Json));
            if (authorityPem is not null) await Put(assets, PublicAsset.AuthorityName, authorityPem);
            else if (await assets.FirstOrDefaultAsync(item => item.Name == PublicAsset.AuthorityName) is { } authority) await assets.DeleteAsync(authority, isHardDelete: true);
            return await settings.SaveChangesAsync();
        });
    }

    /// <summary>写入或覆盖一段公共资产（只登记，不提交）。</summary>
    private static async Task Put(IRepository<PublicAsset> assets, string name, string value)
    {
        var asset = await assets.FirstOrDefaultAsync(item => item.Name == name);
        if (asset is null) await assets.AddAsync(asset = new PublicAsset { Name = name });
        asset.Value = value;
    }
}
