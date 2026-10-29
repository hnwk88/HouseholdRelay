using System.Security.Cryptography.X509Certificates;

namespace Core.Certificates;

/// <summary>服务器证书及发送给客户端的中间链，共同管理其生命周期。</summary>
public sealed class ServerCertificates(X509Certificate2 certificate, X509Certificate2Collection chain) : IDisposable
{
    /// <summary>TLS 握手时出示的服务器叶证书（需带私钥）；其释放由创建方负责，<see cref="Dispose"/> 不会释放它。</summary>
    public X509Certificate2 Certificate { get; } = certificate;

    /// <summary>随握手一起发送给客户端的中间证书链，不含叶证书；由本对象持有并在释放时逐个释放。</summary>
    public X509Certificate2Collection Chain { get; } = chain;

    /// <summary>只释放 <see cref="Chain"/> 中的各个证书，叶证书 <see cref="Certificate"/> 保持不动。</summary>
    public void Dispose() { foreach (var item in Chain) item.Dispose(); }
}
