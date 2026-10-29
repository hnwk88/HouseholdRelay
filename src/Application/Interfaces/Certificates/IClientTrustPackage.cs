namespace Application.Interfaces.Certificates;

/// <summary>生成供客户端下载的公共信任包：私有 CA 公共证书、安装脚本与说明，不含任何秘密。</summary>
public interface IClientTrustPackage
{
    /// <summary>以给定 CA 公共 PEM 生成 ZIP 字节。</summary>
    byte[] Create(string authorityPem);
}
