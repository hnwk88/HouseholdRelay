using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Application.Interfaces.Certificates;

namespace Infrastructure.Security;

/// <summary>签发本实例私有 CA 与服务器证书，并以 Schannel 可用的方式加载 PFX；不向任何系统信任存储安装证书。</summary>
internal sealed class CertificateFactory : ICertificateFactory
{
    /// <summary>私有 CA 的有效期：5 年。</summary>
    private const int AuthorityYears = 5;

    /// <summary>服务器证书的有效期：2 年；到期前 30 天起各页提示重签。</summary>
    private const int ServerYears = 2;

    /// <summary>新建一个私有 CA 并用它签发一张服务器证书，输出该 CA 的公共 PEM；CA 私钥随 CA 对象一起释放，不留存（测试造证书用）。</summary>
    public X509Certificate2 CreateCertificate(string host, out string authorityPem)
    {
        using var authority = CreateAuthority();
        authorityPem = authority.ExportCertificatePem();
        return IssueServer(authority, host);
    }

    /// <summary>新建一个私有 CA（RSA 2048，5 年），返回带私钥、私钥可导出的 CA 证书；调用方负责把私钥受保护地存下来并释放对象。</summary>
    public X509Certificate2 CreateAuthority()
    {
        using var authorityKey = RSA.Create(2048);
        var authorityRequest = new CertificateRequest("CN=Household Relay Private CA", authorityKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        authorityRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        authorityRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        authorityRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(authorityRequest.PublicKey, false));
        using var created = authorityRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(AuthorityYears));
        // 自签结果的私钥在 Windows 上默认不可导出：经 PKCS#12 往返一次换成可导出的临时密钥，才能加密存进库里。
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
    }

    /// <summary>用给定的私有 CA（须带私钥）为 <paramref name="host"/> 签发一张服务器证书（RSA 2048，2 年），返回带私钥的服务器证书；CA 不变，客户端已装的信任照常有效。</summary>
    public X509Certificate2 IssueServer(X509Certificate2 authority, string host)
    {
        if (!authority.HasPrivateKey) throw new InvalidOperationException("私有 CA 缺少私钥，不能签发服务器证书。");
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
        // 服务器证书不能比 CA 活得久：CA 剩下不到 2 年时，服务器证书跟着 CA 一起到期。
        var notAfter = DateTimeOffset.UtcNow.AddYears(ServerYears);
        if (notAfter > authority.NotAfter.ToUniversalTime()) notAfter = authority.NotAfter.ToUniversalTime();
        using var issued = request.Create(authority, DateTimeOffset.UtcNow.AddMinutes(-5), notAfter, RandomNumberGenerator.GetBytes(16));
        return issued.CopyWithPrivateKey(rsa);
    }

    /// <summary>思域代理 CA 的有效期：5 年，与站点 CA 一样；换 CA 时结束全部思域会话、重新准备。</summary>
    private const int ProxyAuthorityYears = 5;

    /// <summary>思域叶证书的有效期：30 天；服务在内存里缓存，快到期就用同一个 CA 重签，客户端信任不受影响。</summary>
    private const int ProxyLeafDays = 30;

    /// <summary>新建带 Name Constraints 的思域代理 CA（RSA 2048，5 年，pathlen 0），私钥可导出，供调用方经 DPAPI 存进 secrets 表。</summary>
    public X509Certificate2 CreateProxyAuthority(IReadOnlyList<string> permittedHosts)
    {
        ArgumentNullException.ThrowIfNull(permittedHosts);
        if (permittedHosts.Count == 0 || permittedHosts.Any(host => string.IsNullOrEmpty(host) || IPAddress.TryParse(host, out _)))
            throw new ArgumentException("代理 CA 只能约束到非空的 DNS 主机名。", nameof(permittedHosts));
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Household Relay Siyu Proxy CA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(NameConstraints(permittedHosts));
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(ProxyAuthorityYears));
        // 与站点 CA 一样：经 PKCS#12 往返一次换成可导出的临时密钥，才能加密存进库里。
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
    }

    /// <summary>给一个官方主机签思域叶证书（RSA 2048，30 天，不超过 CA 有效期）：SAN 只有这一个 DNS 名；经 PKCS#12 按当前用户密钥容器加载，Schannel 才能用它握手。</summary>
    public X509Certificate2 IssueProxyLeaf(X509Certificate2 authority, string host)
    {
        ArgumentNullException.ThrowIfNull(authority); ArgumentException.ThrowIfNullOrEmpty(host);
        if (!authority.HasPrivateKey) throw new InvalidOperationException("思域代理 CA 缺少私钥，不能签发叶证书。");
        if (IPAddress.TryParse(host, out _)) throw new ArgumentException("思域叶证书只签 DNS 主机名。", nameof(host));
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=" + host, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName(host);
        request.CertificateExtensions.Add(san.Build(true));
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(authority, true, true));
        var notAfter = DateTimeOffset.UtcNow.AddDays(ProxyLeafDays);
        if (notAfter > authority.NotAfter.ToUniversalTime()) notAfter = authority.NotAfter.ToUniversalTime();
        using var issued = request.Create(authority, DateTimeOffset.UtcNow.AddMinutes(-5), notAfter, RandomNumberGenerator.GetBytes(16));
        using var withKey = issued.CopyWithPrivateKey(rsa);
        // 直接用内存里的临时密钥给 Schannel 握手会在握手中途断开（order-152 实测 EOF），按服务器证书的加载方式换成用户密钥容器。
        return LoadServerCertificate(withKey.Export(X509ContentType.Pkcs12));
    }

    /// <summary>Name Constraints 扩展（OID 2.5.29.30，critical）：permittedSubtrees 里每个主机一个 dNSName 子树，没有 excludedSubtrees。</summary>
    private static X509Extension NameConstraints(IReadOnlyList<string> hosts)
    {
        var writer = new System.Formats.Asn1.AsnWriter(System.Formats.Asn1.AsnEncodingRules.DER);
        using (writer.PushSequence())
        using (writer.PushSequence(new System.Formats.Asn1.Asn1Tag(System.Formats.Asn1.TagClass.ContextSpecific, 0, true)))
            foreach (var host in hosts)
                using (writer.PushSequence())
                    writer.WriteCharacterString(System.Formats.Asn1.UniversalTagNumber.IA5String, host, new System.Formats.Asn1.Asn1Tag(System.Formats.Asn1.TagClass.ContextSpecific, 2));
        return new X509Extension("2.5.29.30", writer.Encode(), true);
    }

    // Windows Schannel needs a user key container. Without PersistKeySet the framework
    // removes its temporary key on disposal; no certificate is installed in a trust store.
    /// <summary>以当前用户密钥容器加载服务器证书。</summary>
    public X509Certificate2 LoadServerCertificate(byte[] pfx) =>
        X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet);
}
