using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Application.Interfaces.Certificates;

namespace Infrastructure.Security;

/// <summary>签发本实例私有 CA 与服务器证书，并以 Schannel 可用的方式加载 PFX；不向任何系统信任存储安装证书。</summary>
internal sealed class CertificateFactory : ICertificateFactory
{
    /// <summary>签发一张由新私有 CA 签名的服务器证书，并输出该 CA 的公共 PEM。</summary>
    public X509Certificate2 CreateCertificate(string host, out string authorityPem)
    {
        using var authorityKey = RSA.Create(2048);
        var authorityRequest = new CertificateRequest("CN=Household Relay Private CA", authorityKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        authorityRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        authorityRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        authorityRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(authorityRequest.PublicKey, false));
        using var authority = authorityRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(5));
        authorityPem = authority.ExportCertificatePem();
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Household Relay", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); san.AddIpAddress(IPAddress.Loopback);
        if (IPAddress.TryParse(host, out var ip)) { if (!ip.Equals(IPAddress.Loopback)) san.AddIpAddress(ip); }
        else if (host != "localhost") san.AddDnsName(host);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        // 私有 CA 名称可能相同；明确签发者的密钥和序列号，防止 TLS 链选择同名的另一证书。
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(authority, true, true));
        using var issued = request.Create(authority, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(2), RandomNumberGenerator.GetBytes(16));
        return issued.CopyWithPrivateKey(rsa);
    }

    // Windows Schannel needs a user key container. Without PersistKeySet the framework
    // removes its temporary key on disposal; no certificate is installed in a trust store.
    /// <summary>以当前用户密钥容器加载服务器证书。</summary>
    public X509Certificate2 LoadServerCertificate(byte[] pfx) =>
        X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet);
}
