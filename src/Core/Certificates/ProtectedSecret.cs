using Repositories;

namespace Core.Certificates;

/// <summary>
/// 按名字（唯一）存的一份 DPAPI 密文（表 secrets）：服务器证书 <c>tls</c>、私有 CA（含私钥）<c>ca</c>，以及覆盖前留下的上一份（名字加 <see cref="PreviousSuffix"/>）。
/// </summary>
public sealed class ProtectedSecret : BaseEntity
{
    /// <summary>服务器证书 PFX 密文的名字。</summary>
    public const string ServerCertificate = "tls";

    /// <summary>私有 CA（证书连同私钥的 PFX）密文的名字；换地址或续期只用它重签服务器证书。旧版建的私有 CA 没有这一份。</summary>
    public const string Authority = "ca";

    /// <summary>
    /// 思域代理 CA（证书连同私钥的 PFX）密文的名字：和站点私有 CA 是两套，只给思域进程信任；带 Name Constraints，只能签两个官方主机。
    /// 不留上一份：每次建思域会话都把当前 CA 公钥原样交给 MyCoAI，换了 CA 只影响新会话。
    /// </summary>
    public const string SiyuProxyAuthority = "siyu_proxy_ca";

    /// <summary>上一份的名字后缀：导入或新建证书覆盖之前，把当前那份存成「名字.previous」，可以退回。</summary>
    public const string PreviousSuffix = ".previous";

    /// <summary>名字。</summary>
    public string Name { get; set; } = "";

    /// <summary>DPAPI 密文。</summary>
    public byte[] Protected { get; set; } = [];
}
