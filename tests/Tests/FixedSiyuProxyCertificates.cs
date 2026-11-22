using System.Security.Cryptography.X509Certificates;
using Application.Interfaces.Siyu;
using Core.Siyu;

namespace Tests;

/// <summary>测试用叶证书来源：启动时签好两张，按主机给出。</summary>
public sealed class FixedSiyuProxyCertificates(IReadOnlyDictionary<OfficialHost, X509Certificate2> leaves) : ISiyuProxyCertificates
{
    /// <summary>按主机给叶证书。</summary>
    public X509Certificate2 Leaf(OfficialHost host) => leaves[host];
}
