using Core.Certificates;

namespace Application.Interfaces.Certificates;

/// <summary>HTTPS 入口证书：创建私有 CA、导入可信 PFX、读取展示信息，并为启动加载经过校验的证书链。</summary>
public interface ICertificateService
{
    /// <summary>可展示的证书状态；未配置时为空。</summary>
    CertificateInfo? ReadInfo();

    /// <summary>校验并导入系统信任的服务器证书 PFX，同时保存对外地址和监听端口。</summary>
    void Import(long settingsVersion, string publicUrl, int listenPort, byte[] pfx, string? password);

    /// <summary>加载并重新校验当前证书及其链；未配置时为空。</summary>
    ServerCertificates? LoadForServing();

    /// <summary>私有 CA 的公共 PEM；没有私有 CA 时为空。</summary>
    string? ReadAuthorityPem();

    /// <summary>私有 CA 的 SHA-256 指纹，供客户端安装信任前核对。</summary>
    string? AuthorityFingerprint();

    /// <summary>签发新的私有 CA 与服务器证书；已有证书时须明确确认替换。</summary>
    void CreatePrivate(long settingsVersion, string publicUrl, int listenPort, bool replaceExisting = false);
}
