using System.Diagnostics.CodeAnalysis;

namespace Core.Configuration;

/// <summary>「对外地址必须是 HTTPS 根地址」只在这里判一次：保存设置与签发、导入证书都调它，各自读出自己的那句提示。</summary>
public static class PublicAddress
{
    /// <summary>是不是 HTTPS 根地址：绝对地址、https、不含账号密码、查询、片段，路径只能是「/」。是的话交出解析好的地址。</summary>
    public static bool IsHttpsRoot(string? value, [NotNullWhen(true)] out Uri? url)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out url) && url.Scheme == "https" && url.UserInfo.Length == 0
            && url.Query.Length == 0 && url.Fragment.Length == 0 && url.AbsolutePath == "/") return true;
        url = null; return false;
    }
}
