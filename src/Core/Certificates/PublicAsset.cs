using Repositories;

namespace Core.Certificates;

/// <summary>
/// 按名字（唯一）存的一段可公开文本（表 public_assets）：证书展示信息 <c>tls_info</c>、私有 CA 公共 PEM <c>legacy_ca</c>、证书对应的对外地址与端口 <c>tls_address</c>，
/// 以及覆盖前留下的上一份（名字加 <see cref="ProtectedSecret.PreviousSuffix"/>）。
/// </summary>
public sealed class PublicAsset : BaseEntity
{
    /// <summary>证书展示信息（<see cref="CertificateInfo"/> 的 JSON）的名字。</summary>
    public const string CertificateInfoName = "tls_info";

    /// <summary>私有 CA 公共 PEM 的名字；导入可信证书后不再有当前这一份（上一份留着，可以退回）。</summary>
    public const string AuthorityName = "legacy_ca";

    /// <summary>证书保存时一起写进设置的对外地址与监听端口（JSON）的名字；只为退回上一份证书时把地址一起退回。</summary>
    public const string AddressName = "tls_address";

    /// <summary>名字。</summary>
    public string Name { get; set; } = "";

    /// <summary>文本值。</summary>
    public string Value { get; set; } = "";
}
