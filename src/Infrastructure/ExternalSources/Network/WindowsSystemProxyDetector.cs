using System.Net;
using Microsoft.Win32;

namespace Infrastructure.ExternalSources.Network;

/// <summary>
/// 探测 Windows 当前实际生效的代理配置（照 MyCoAI 同名类型移植）：借 <see cref="HttpClient.DefaultProxy"/> 依次读取代理环境变量、
/// 系统「设置 → 网络和 Internet → 代理」、PAC 自动配置脚本与 WPAD 结果，本类不重新实现任何一层。
/// <see cref="IWebProxy.GetProxy"/> 只在本机解析配置，不向目标发业务请求，可以在每次建连前重新探测。
/// </summary>
public static class WindowsSystemProxyDetector
{
    /// <summary>
    /// 探测系统对 <paramref name="target"/> 生效的代理。<paramref name="systemProxy"/> 省略时用 <see cref="HttpClient.DefaultProxy"/>；
    /// 测试传入假实现以精确控制 <see cref="IWebProxy.IsBypassed"/> 与 <see cref="IWebProxy.GetProxy"/> 的返回值，不依赖测试机真实的系统代理。
    /// </summary>
    public static ProxyDetectionResult Detect(Uri target, IWebProxy? systemProxy = null)
    {
        var proxy = systemProxy ?? HttpClient.DefaultProxy;
        return new ProxyDetectionResult(ResolveProxy(proxy, ChangeScheme(target, "http")), ResolveProxy(proxy, ChangeScheme(target, "https")), ReadNoProxy());
    }

    /// <summary>解析单个协议下的代理；系统明确要求直连（<see cref="IWebProxy.IsBypassed"/> 为真）或返回目标本身时都视为没有代理。</summary>
    private static string? ResolveProxy(IWebProxy systemProxy, Uri target)
    {
        if (systemProxy.IsBypassed(target)) return null;
        var proxy = systemProxy.GetProxy(target);
        return proxy is null || SameAddress(proxy, target) ? null : proxy.AbsoluteUri.TrimEnd('/');
    }

    /// <summary>切换目标地址的协议并清空显式端口，避免把原协议的端口带到另一个协议的探测地址里。</summary>
    private static Uri ChangeScheme(Uri original, string scheme) => new UriBuilder(original) { Scheme = scheme, Port = -1 }.Uri;

    /// <summary>比较两个地址的协议、主机、端口和路径是否一致；<see cref="IWebProxy.GetProxy"/> 按约定返回目标本身时表示不用代理。</summary>
    private static bool SameAddress(Uri first, Uri second) =>
        Uri.Compare(first, second, UriComponents.SchemeAndServer | UriComponents.PathAndQuery, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0;

    /// <summary>
    /// 拼出写给子进程的 NO_PROXY：合并本进程已有的 NO_PROXY/no_proxy（保留用户的明确配置）、Windows 手动代理里的
    /// 「不使用代理的地址」（注册表 ProxyOverride，只读不改），再加上本机回环地址兜底，防止本机端点被送进代理。
    /// </summary>
    private static string ReadNoProxy()
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddNoProxyValues(values, Environment.GetEnvironmentVariable("NO_PROXY") ?? Environment.GetEnvironmentVariable("no_proxy"));
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings"))
            AddNoProxyValues(values, key?.GetValue("ProxyOverride") as string);
        values.Add("localhost"); values.Add("127.0.0.1"); values.Add("::1");
        return string.Join(',', values);
    }

    /// <summary>把分号或逗号分隔的地址列表拆开加入集合；<c>&lt;local&gt;</c> 按惯例展开成本机地址，Windows 的 <c>*.example</c> 写法改成标准的 <c>.example</c>。</summary>
    private static void AddNoProxyValues(ISet<string> destination, string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return;
        foreach (var item in source.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (item.Equals("<local>", StringComparison.OrdinalIgnoreCase)) { destination.Add("localhost"); destination.Add("127.0.0.1"); destination.Add("::1"); continue; }
            destination.Add(item.StartsWith("*.", StringComparison.Ordinal) ? item[1..] : item);
        }
    }
}
