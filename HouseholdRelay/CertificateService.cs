using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using static HouseholdRelay.RelayDatabase;

namespace HouseholdRelay;

/// <summary>可展示的证书状态，不包含 PFX、私钥或密码。</summary>
public sealed record CertificateInfo(string Mode, string Host, DateTimeOffset ValidUntil, string Thumbprint, DateTimeOffset CheckedAt);

/// <summary>导入系统信任的服务器证书，复用原 DPAPI 机密与设置事务。</summary>
public sealed class CertificateService(RelayDatabase database)
{
    public CertificateInfo? ReadInfo()
    {
        using var connection = database.Open();
        var json = Scalar(connection, null, "SELECT value FROM public_assets WHERE name='tls_info'") as string;
        return json is null ? null : JsonSerializer.Deserialize<CertificateInfo>(json, ConfigurationFiles.Json);
    }

    public void Import(long settingsVersion, string publicUrl, int listenPort, byte[] pfx, string? password)
        => Import(settingsVersion, publicUrl, listenPort, pfx, password, null);

    internal void Import(long settingsVersion, string publicUrl, int listenPort, byte[] pfx, string? password, X509Certificate2Collection? testRoots)
    {
        if (pfx.Length is 0 or > 1024 * 1024) throw new InvalidOperationException("PFX 文件必须小于 1 MB，且包含服务器私钥和证书链。");
        var config = new ConfigurationService(database).Read().Value;
        config.PublicUrl = publicUrl.Trim(); config.ListenPort = listenPort; config.Validate();
        var url = PublicAddress(config.PublicUrl);
        X509Certificate2Collection certificates;
        try { certificates = X509CertificateLoader.LoadPkcs12Collection(pfx, password, X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable); }
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
                var encrypted = WindowsSecrets.Protect(plain);
                database.Write((connection, transaction) =>
                {
                    if (Execute(connection, transaction, "UPDATE settings SET version=version+1,json=$json WHERE id=1 AND version=$version",
                        ("$json", SerializeSettings(config)), ("$version", settingsVersion)) != 1)
                        throw new InvalidOperationException("项目设置已经改变，请重新加载再导入证书。");
                    Execute(connection, transaction, "INSERT INTO secrets VALUES('tls',$value) ON CONFLICT(name) DO UPDATE SET protected=excluded.protected", ("$value", encrypted));
                    Execute(connection, transaction, "INSERT INTO public_assets VALUES('tls_info',$value) ON CONFLICT(name) DO UPDATE SET value=excluded.value",
                        ("$value", JsonSerializer.Serialize(info, ConfigurationFiles.Json)));
                    return Execute(connection, transaction, "DELETE FROM public_assets WHERE name='legacy_ca'");
                });
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        finally { foreach (var certificate in certificates) certificate.Dispose(); }
    }

    internal static Uri PublicAddress(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url) || url.Scheme != "https" || url.UserInfo.Length != 0
            || url.Query.Length != 0 || url.Fragment.Length != 0 || url.AbsolutePath != "/")
            throw new InvalidOperationException("对外地址必须是 HTTPS 根地址，不含密码、查询参数或路径。");
        return url;
    }

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
            // 仅测试程序集可提供独立信任根；生产入口始终使用系统信任，不修改证书存储。
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust; chain.ChainPolicy.CustomTrustStore.AddRange(testRoots);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck; chain.ChainPolicy.DisableCertificateDownloads = true;
        }
        if (!chain.Build(certificate)) throw new InvalidOperationException("证书链不受系统信任、用途不符或吊销检查未通过，请补齐有效证书链。");
    }

    public ServerCertificates? LoadForServing()
    {
        var pfx = new ConfigurationService(database).LoadCertificate();
        if (pfx is null) return null;
        X509Certificate2Collection certificates;
        try { certificates = X509CertificateLoader.LoadPkcs12Collection(pfx, null, X509KeyStorageFlags.UserKeySet); }
        finally { CryptographicOperations.ZeroMemory(pfx); }
        try
        {
            var certificate = certificates.Cast<X509Certificate2>().Single(item => item.HasPrivateKey);
            if (ReadInfo()?.Mode == "trusted") Validate(certificate, certificates, PublicAddress(new ConfigurationService(database).Read().Value.PublicUrl).DnsSafeHost);
            else
            {
                // 旧实例的 CA 单独保存在 public_assets；发送配套链，避免按同名颁发者猜测证书链。
                using var connection = database.Open();
                if (Scalar(connection, null, "SELECT value FROM public_assets WHERE name='legacy_ca'") is string pem)
                    certificates.Add(X509Certificate2.CreateFromPem(pem));
            }
            return new ServerCertificates(certificate, certificates);
        }
        catch { foreach (var certificate in certificates) certificate.Dispose(); throw; }
    }
}

/// <summary>服务器证书及发送给客户端的中间链，共同管理其生命周期。</summary>
public sealed class ServerCertificates(X509Certificate2 certificate, X509Certificate2Collection chain) : IDisposable
{
    public X509Certificate2 Certificate { get; } = certificate;
    public X509Certificate2Collection Chain { get; } = chain;
    public void Dispose() { foreach (var item in Chain) item.Dispose(); }
}
