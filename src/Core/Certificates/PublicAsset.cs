using Repositories;

namespace Core.Certificates;

/// <summary>按名字（唯一）存的一段可公开文本（表 public_assets）：证书展示信息 <c>tls_info</c>、私有 CA 公共 PEM <c>legacy_ca</c>。</summary>
public sealed class PublicAsset : BaseEntity
{
    /// <summary>证书展示信息（<see cref="CertificateInfo"/> 的 JSON）的名字。</summary>
    public const string CertificateInfoName = "tls_info";

    /// <summary>私有 CA 公共 PEM 的名字；导入可信证书后物理删除。</summary>
    public const string AuthorityName = "legacy_ca";

    /// <summary>名字。</summary>
    public string Name { get; set; } = "";

    /// <summary>文本值。</summary>
    public string Value { get; set; } = "";
}
