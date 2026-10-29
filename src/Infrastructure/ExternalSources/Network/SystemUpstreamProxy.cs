using System.Net;
using Application.Interfaces.Upstream;
using Core.Configuration;
using Core.Upstream;

namespace Infrastructure.ExternalSources.Network;

/// <summary>
/// 全局出站代理的实现：每次解析都重读设置，改完设置后新建立的上游连接立即按新方式走。
/// 失败关闭照 MyCoAI 的代码而不是它的旧注释：已启用却取不到可用地址时一律拒绝，绝不悄悄直连。
/// </summary>
public sealed class SystemUpstreamProxy(Func<RelayConfiguration> settings, IWebProxy? systemProxy = null) : IUpstreamProxy
{
    private const string Loopback = "localhost,127.0.0.1,::1";

    /// <summary>
    /// 按当前设置为 <paramref name="target"/> 解析代理：<c>none</c> 直连；<c>manual</c> 用设置里的地址；<c>auto</c> 用系统对该目标生效的代理（HTTPS 目标优先取 HTTPS 代理）。
    /// 手动未填地址、自动检测不到代理、或解析出的地址不是 HTTP 根地址时抛出 <see cref="UpstreamProxyUnavailableException"/>。
    /// </summary>
    public ProxyRoute Resolve(Uri target)
    {
        var config = settings();
        switch (config.ProxyMode)
        {
            case "manual":
                if (string.IsNullOrWhiteSpace(config.ProxyUrl))
                    throw new UpstreamProxyUnavailableException("已选择手动指定代理，但没有填写代理地址；为防止意外直连，请求没有发出。请到项目设置填写代理地址。");
                var manual = Checked(config.ProxyUrl, "手动指定的代理地址");
                return new(manual, Loopback, "手动指定 " + Text(manual));
            case "auto":
                var detected = WindowsSystemProxyDetector.Detect(target, systemProxy);
                var found = target.Scheme == "https" ? detected.HttpsProxy ?? detected.HttpProxy : detected.HttpProxy ?? detected.HttpsProxy;
                if (string.IsNullOrWhiteSpace(found))
                    throw new UpstreamProxyUnavailableException("已选择自动检测系统代理，但系统没有为上游地址提供代理；为防止意外直连，请求没有发出。请打开代理软件，或到项目设置改成不使用代理、手动指定地址。");
                var automatic = Checked(found, "自动检测到的系统代理");
                return new(automatic, detected.NoProxy ?? Loopback, "自动检测到 " + Text(automatic));
            default:
                return new(null, Loopback, "不使用代理，直连上游");
        }
    }

    /// <summary>以官方接口地址为探测目标读出当前方式与结果，例如「自动检测到 http://127.0.0.1:7897」；取不到时读出原因，不抛异常。</summary>
    public string Describe()
    {
        try { return Resolve(new Uri("https://" + UpstreamDetection.OfficialHost + "/")).Description + "。"; }
        // 取不到代理是要读给管理员听的状态，不是页面错误。
        catch (UpstreamProxyUnavailableException error) { return error.Message; }
    }

    /// <summary>补齐 http:// 后按中转的代理地址规则校验（只接受不含账号的 HTTP 根地址）；不合规则时转成读得懂的拒绝原因。</summary>
    private static Uri Checked(string value, string source)
    {
        var trimmed = value.Trim();
        var normalized = trimmed.Contains("://", StringComparison.Ordinal) ? trimmed : "http://" + trimmed;
        try { return UpstreamAddress.Proxy(normalized); }
        // 地址格式不被接受同样不能退回直连，只换成带来源的说明。
        catch (InvalidOperationException) { throw new UpstreamProxyUnavailableException($"{source} {Text(trimmed)} 不是 HTTP 代理根地址，中转不能使用；为防止意外直连，请求没有发出。请到项目设置检查代理。"); }
    }

    /// <summary>朗读用的代理地址：去掉末尾斜杠。</summary>
    private static string Text(Uri value) => value.AbsoluteUri.TrimEnd('/');

    /// <summary>朗读用的原始代理文字：截到 200 字以内，防止异常长的值淹没提示。</summary>
    private static string Text(string value) => value.Length > 200 ? value[..200] : value;
}
