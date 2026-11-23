using Core.Certificates;

namespace Application.Interfaces.Certificates;

/// <summary>
/// HTTPS 入口证书：创建私有 CA、用存下来的私有 CA 重签服务器证书、导入可信 PFX、退回上一份、读取展示信息，并为启动加载经过校验的证书链。
/// 任何一次覆盖证书之前都把当前那一份（证书、CA、展示信息、地址）存成上一份。
/// </summary>
public interface ICertificateService
{
    /// <summary>可展示的证书状态；未配置时为空。</summary>
    Task<CertificateInfo?> ReadInfoAsync();

    /// <summary>校验并导入系统信任的服务器证书 PFX，同时保存对外地址和监听端口；已有私有 CA 时拒绝（覆盖私有 CA 须走带确认的那个重载）。</summary>
    Task ImportAsync(long settingsVersion, string publicUrl, int listenPort, byte[] pfx, string? password);

    /// <summary>同上；<paramref name="replacePrivateConfirmed"/> 为真表示管理员已勾选「导入后装了私有 CA 的客户端要改信任新证书」，已有私有 CA 时才放行。</summary>
    Task ImportAsync(long settingsVersion, string publicUrl, int listenPort, byte[] pfx, string? password, bool replacePrivateConfirmed);

    /// <summary>加载并重新校验当前证书及其链；未配置时为空。</summary>
    Task<ServerCertificates?> LoadForServingAsync();

    /// <summary>私有 CA 的公共 PEM；没有私有 CA 时为空。</summary>
    Task<string?> ReadAuthorityPemAsync();

    /// <summary>私有 CA 的 SHA-256 指纹，供客户端安装信任前核对。</summary>
    Task<string?> AuthorityFingerprintAsync();

    /// <summary>当前是私有 CA，且 CA 私钥受 DPAPI 保护存着：可以只重签服务器证书。旧版建的私有 CA 没有私钥，为假。</summary>
    Task<bool> CanReissueAsync();

    /// <summary>有没有上一份证书可以退回。</summary>
    Task<bool> HasPreviousAsync();

    /// <summary>签发新的私有 CA 与服务器证书，CA 私钥受 DPAPI 保护存下；已有证书时须明确确认替换（之后每台客户端要重新安装信任）。</summary>
    Task CreatePrivateAsync(long settingsVersion, string publicUrl, int listenPort, bool replaceExisting = false);

    /// <summary>用存下来的私有 CA 为（新的）对外地址重签服务器证书并保存地址与端口：CA 指纹不变，客户端不用重装信任。没有存着 CA 私钥时拒绝并读出下一步。</summary>
    Task ReissuePrivateAsync(long settingsVersion, string publicUrl, int listenPort);

    /// <summary>退回上一份证书：当前那份与上一份对调（含 CA、展示信息与对外地址、端口），再退回一次就回到现在这份。没有上一份时拒绝。</summary>
    Task RestorePreviousAsync(long settingsVersion);
}
