using System.Security.Cryptography.X509Certificates;

namespace Tests;

/// <summary>
/// 清掉本次测试运行往当前用户「中间证书颁发机构」存储里留下的测试 CA。
/// TLS 用例用私有 CA 签发的服务器证书起 Kestrel 时，Windows 构建证书链会把链上的 CA 公钥证书自动放进这个存储；
/// 不清理就会越积越多，攒到几十张同名证书后链构建直接报错，TLS 用例随之失败。
/// </summary>
static class TestCertificateStore
{
    /// <summary>测试 CA 的主体名，与生产的私有 CA 同名，所以只能按「本次运行前没有」来区分。</summary>
    private const string AuthoritySubject = "CN=Household Relay Private CA";

    /// <summary>记下运行开始前存储里已有的同名证书指纹；这些可能是本机真正在用的中转 CA，清理时一律保留。</summary>
    public static HashSet<string> Snapshot()
    {
        using var store = new X509Store(StoreName.CertificateAuthority, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        return store.Certificates.Where(IsAuthority).Select(certificate => certificate.Thumbprint).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>删除本次运行新增的同名测试 CA，返回删了几张；开始前就在的证书不动。</summary>
    public static int RemoveAddedSince(HashSet<string> before)
    {
        using var store = new X509Store(StoreName.CertificateAuthority, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        var added = store.Certificates.Where(certificate => IsAuthority(certificate) && !before.Contains(certificate.Thumbprint)).ToArray();
        foreach (var certificate in added) store.Remove(certificate);
        return added.Length;
    }

    /// <summary>只认主体名完全等于测试 CA 主体名的证书，别的 CA 一概不碰。</summary>
    private static bool IsAuthority(X509Certificate2 certificate) => string.Equals(certificate.Subject, AuthoritySubject, StringComparison.Ordinal);
}
