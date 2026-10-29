using Core.Configuration;

namespace Application.Interfaces.Configuration;

/// <summary>管理配置的唯一写入路径；网页和命令行共用，设置版本冲突须由操作者重新加载后决定。</summary>
public interface IConfigurationService
{
    /// <summary>读取当前设置及版本号。</summary>
    SettingsSnapshot Read();

    /// <summary>校验对外地址与已导入证书的域名一致后，按版本号保存设置。</summary>
    void Save(long version, RelayConfiguration config);

    /// <summary>活动上游是否已保存凭据。</summary>
    bool HasCredential { get; }

    /// <summary>是否已保存服务器证书。</summary>
    bool HasCertificate { get; }

    /// <summary>解密并返回活动上游凭据；未配置时为空。</summary>
    string? LoadCredential();

    /// <summary>解密并返回服务器证书 PFX 字节；调用方用完后须清零。</summary>
    byte[]? LoadCertificate();

    /// <summary>以当前活动配置加新凭据保存为待测试候选，不直接改变活动上游。</summary>
    void SaveCredential(string credential);

    /// <summary>命令行 init：按主机名与端口创建私有 CA 及服务器证书。</summary>
    void ConfigureLegacyTls(string host, int publicPort, int listenPort);
}
