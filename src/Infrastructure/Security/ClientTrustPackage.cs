using System.IO.Compression;
using System.Text;
using Application.Interfaces.Certificates;

namespace Infrastructure.Security;

/// <summary>从公共 CA 和随程序发布的固定模板生成无秘密的客户端信任包。</summary>
internal sealed class ClientTrustPackage : IClientTrustPackage
{
    /// <summary>以 CA 公共 PEM 和嵌入的安装脚本、说明生成 ZIP；资源名沿用 HouseholdRelay.ClientTrust.文件名。</summary>
    public byte[] Create(string authorityPem)
    {
        using var authority = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(authorityPem);
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            // 重新导出公共证书，拒绝让数据库资产中的其他内容进入下载包。
            using (var writer = new StreamWriter(archive.CreateEntry("relay-ca.pem").Open(), Encoding.ASCII))
                writer.Write(authority.ExportCertificatePem());
            foreach (var name in new[] { "Install-RelayTrust.ps1", "README.txt" })
            {
                using var source = typeof(ClientTrustPackage).Assembly.GetManifestResourceStream("HouseholdRelay.ClientTrust." + name)
                    ?? throw new InvalidOperationException("客户端信任模板缺失，请重新发布程序。");
                using var target = archive.CreateEntry(name).Open(); source.CopyTo(target);
            }
        }
        return stream.ToArray();
    }
}
