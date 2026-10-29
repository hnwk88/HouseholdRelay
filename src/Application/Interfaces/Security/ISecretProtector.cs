namespace Application.Interfaces.Security;

/// <summary>本机机密保护：加密保存凭据、证书和引导码，并把数据目录限制为当前用户与系统账户可访问。</summary>
public interface ISecretProtector
{
    /// <summary>按当前 Windows 用户加密一段明文，调用方负责清零原明文。</summary>
    byte[] Protect(byte[] value);

    /// <summary>解密由 <see cref="Protect"/> 产生的密文，调用方用完后须清零返回值。</summary>
    byte[] Unprotect(byte[] value);

    /// <summary>创建目录并收紧访问控制，只保留当前用户与本机系统账户。</summary>
    void ProtectDirectory(string path);
}
