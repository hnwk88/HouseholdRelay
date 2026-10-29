using Core.Certificates;
using Core.Configuration;

namespace Application.Interfaces.Certificates;

/// <summary>服务器证书机密与公共展示信息的持久化；保存证书时在同一事务里按版本号更新设置。</summary>
public interface ICertificateRepository
{
    /// <summary>读取加密保存的服务器证书 PFX；未配置时为空。</summary>
    byte[]? ReadProtectedCertificate();

    /// <summary>读取已记录的证书展示信息；迁移来的旧证书可能没有。</summary>
    CertificateInfo? ReadStoredInfo();

    /// <summary>读取私有 CA 的公共 PEM；导入可信证书后为空。</summary>
    string? ReadAuthorityPem();

    /// <summary>保存导入的可信证书、展示信息和新设置，并删除旧私有 CA；设置版本已变化时拒绝。</summary>
    void SaveImported(long settingsVersion, RelayConfiguration config, byte[] protectedPfx, CertificateInfo info);

    /// <summary>保存新签发的私有证书、展示信息、新设置和私有 CA 公共 PEM；设置版本已变化时拒绝。</summary>
    void SavePrivate(long settingsVersion, RelayConfiguration config, byte[] protectedPfx, CertificateInfo info, string authorityPem);
}
