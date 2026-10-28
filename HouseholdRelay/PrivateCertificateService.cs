using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using static HouseholdRelay.RelayDatabase;

namespace HouseholdRelay;

/// <summary>在已有配置事务内签发私有证书，并仅向客户端提供公共信任材料。</summary>
public sealed partial class CertificateService
{
    public string? ReadAuthorityPem()
    {
        using var connection = database.Open();
        return Scalar(connection, null, "SELECT value FROM public_assets WHERE name='legacy_ca'") as string;
    }

    public string? AuthorityFingerprint()
    {
        var pem = ReadAuthorityPem();
        if (pem is null) return null;
        using var authority = X509Certificate2.CreateFromPem(pem);
        return authority.GetCertHashString(HashAlgorithmName.SHA256);
    }

    public void CreatePrivate(long settingsVersion, string publicUrl, int listenPort, bool replaceExisting = false)
    {
        var settings = new ConfigurationService(database);
        if (settings.HasCertificate && !replaceExisting)
            throw new InvalidOperationException("已有证书。更换将生成新的信任根，须确认每台客户端重新安装信任。");
        var config = settings.Read().Value;
        config.PublicUrl = publicUrl.Trim(); config.ListenPort = listenPort; config.Validate();
        var address = PublicAddress(config.PublicUrl);
        using var leaf = Management.CreateCertificate(address.DnsSafeHost, out var pem);
        using var authority = X509Certificate2.CreateFromPem(pem);
        Validate(leaf, new X509Certificate2Collection(authority), address.DnsSafeHost, new X509Certificate2Collection(authority));
        var plain = leaf.Export(X509ContentType.Pkcs12);
        try
        {
            var encrypted = WindowsSecrets.Protect(plain);
            var info = new CertificateInfo("private", address.DnsSafeHost, leaf.NotAfter.ToUniversalTime(), leaf.Thumbprint, DateTimeOffset.UtcNow);
            database.Write((connection, transaction) =>
            {
                if (Execute(connection, transaction, "UPDATE settings SET version=version+1,json=$json WHERE id=1 AND version=$version",
                    ("$json", SerializeSettings(config)), ("$version", settingsVersion)) != 1)
                    throw new InvalidOperationException("项目设置已经改变，请重新加载后创建证书。");
                Execute(connection, transaction, "INSERT INTO secrets VALUES('tls',$value) ON CONFLICT(name) DO UPDATE SET protected=excluded.protected", ("$value", encrypted));
                Execute(connection, transaction, "INSERT INTO public_assets VALUES('tls_info',$value) ON CONFLICT(name) DO UPDATE SET value=excluded.value",
                    ("$value", JsonSerializer.Serialize(info, ConfigurationFiles.Json)));
                return Execute(connection, transaction, "INSERT INTO public_assets VALUES('legacy_ca',$value) ON CONFLICT(name) DO UPDATE SET value=excluded.value", ("$value", pem));
            });
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}
