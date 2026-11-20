using Repositories;

namespace Core.Certificates;

/// <summary>按名字（唯一）存的一份 DPAPI 密文（表 secrets），今天只有服务器证书 <c>tls</c>。</summary>
public sealed class ProtectedSecret : BaseEntity
{
    /// <summary>服务器证书 PFX 密文的名字。</summary>
    public const string ServerCertificate = "tls";

    /// <summary>名字。</summary>
    public string Name { get; set; } = "";

    /// <summary>DPAPI 密文。</summary>
    public byte[] Protected { get; set; } = [];
}
