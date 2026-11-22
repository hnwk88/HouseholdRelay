using System.Security.Cryptography.X509Certificates;
using Application.Interfaces.Siyu;
using Core.Siyu;

namespace Tests;

/// <summary>测试用代理证书来源：启动时签好两张叶证书，按主机给出；代理 CA 只给公钥证书。</summary>
public sealed class FixedSiyuProxyCertificates(X509Certificate2 authority, IReadOnlyDictionary<OfficialHost, X509Certificate2> leaves) : ISiyuProxyCertificates
{
    /// <summary>按主机给叶证书。</summary>
    public X509Certificate2 Leaf(OfficialHost host) => leaves[host];

    /// <summary>代理 CA 的公钥证书。</summary>
    public X509Certificate2 Authority { get; } = X509CertificateLoader.LoadCertificate(authority.RawData);
}
