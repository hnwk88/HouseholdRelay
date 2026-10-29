using Application.Interfaces.Security;

namespace Infrastructure.Security;

/// <summary>以当前 Windows 用户范围的 DPAPI 和收紧的目录 ACL 实现机密保护。</summary>
internal sealed class DpapiSecretProtector : ISecretProtector
{
    /// <summary>DPAPI 加密。</summary>
    public byte[] Protect(byte[] value) => WindowsSecrets.Protect(value);

    /// <summary>DPAPI 解密。</summary>
    public byte[] Unprotect(byte[] value) => WindowsSecrets.Unprotect(value);

    /// <summary>创建目录并只保留当前用户与本机系统账户的访问权限。</summary>
    public void ProtectDirectory(string path) => WindowsSecrets.ProtectDirectory(path);
}
