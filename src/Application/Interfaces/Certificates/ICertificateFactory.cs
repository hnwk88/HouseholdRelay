using System.Security.Cryptography.X509Certificates;

namespace Application.Interfaces.Certificates;

/// <summary>签发本实例私有 CA 与服务器证书，并以服务端 TLS 可用的方式加载 PFX；不向系统信任存储安装任何证书。</summary>
public interface ICertificateFactory
{
    /// <summary>为主机名签发一张由新私有 CA 签名的服务器证书，同时输出该 CA 的公共 PEM；CA 私钥不留存（测试造证书用）。</summary>
    X509Certificate2 CreateCertificate(string host, out string authorityPem);

    /// <summary>新建一个私有 CA，返回带可导出私钥的 CA 证书；调用方把私钥受保护地存下来，以后换地址或续期只用它重签服务器证书。</summary>
    X509Certificate2 CreateAuthority();

    /// <summary>用给定的私有 CA（须带私钥）为主机名签发服务器证书；CA 不变，客户端已装的信任照常有效。</summary>
    X509Certificate2 IssueServer(X509Certificate2 authority, string host);

    /// <summary>以当前用户密钥容器加载带私钥的服务器证书。</summary>
    X509Certificate2 LoadServerCertificate(byte[] pfx);
}
