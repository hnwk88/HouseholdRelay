using System.Security.Cryptography.X509Certificates;
using Core.Siyu;

namespace Application.Interfaces.Siyu;

/// <summary>思域内层 TLS 用的叶证书：由思域代理 CA 签、SAN 只有那一个官方主机，快到期时自动用同一个 CA 重签。</summary>
public interface ISiyuProxyCertificates
{
    /// <summary>给 <paramref name="host"/> 的带私钥叶证书。</summary>
    X509Certificate2 Leaf(OfficialHost host);
}
