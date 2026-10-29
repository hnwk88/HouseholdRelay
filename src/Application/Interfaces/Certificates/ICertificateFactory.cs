using System.Security.Cryptography.X509Certificates;

namespace Application.Interfaces.Certificates;

/// <summary>签发本实例私有 CA 与服务器证书，并以服务端 TLS 可用的方式加载 PFX；不向系统信任存储安装任何证书。</summary>
public interface ICertificateFactory
{
    /// <summary>为主机名签发一张由新私有 CA 签名的服务器证书，同时输出该 CA 的公共 PEM。</summary>
    X509Certificate2 CreateCertificate(string host, out string authorityPem);

    /// <summary>以当前用户密钥容器加载带私钥的服务器证书。</summary>
    X509Certificate2 LoadServerCertificate(byte[] pfx);
}
