using Core.Configuration;

namespace Core.Upstream;

/// <summary>一个候选版本的非秘密配置；地址和出站代理分别建模。</summary>
public sealed record UpstreamOptions
{
    /// <summary>凭据类型：<c>oauth</c>（Claude 账户令牌）或 <c>api-key</c>（API 密钥）。</summary>
    public string Mode { get; init; } = "api-key";

    /// <summary>凭据放在哪个请求头：<c>bearer</c>（Authorization: Bearer）或 <c>x-api-key</c>；账户模式只能用 bearer。</summary>
    public string Authentication { get; init; } = "x-api-key";

    /// <summary>上游 API 根地址；经 <see cref="Validated"/> 规范化后去掉末尾的 <c>/</c> 和 <c>/v1</c>。</summary>
    public string BaseUrl { get; init; } = "https://api.anthropic.com";

    /// <summary>是否在请求里附加账户 OAuth beta 标记；仅账户模式有效，官方预设下会被强制开启。</summary>
    public bool OAuthBeta { get; init; }

    /// <summary>上游预设：<c>anthropic</c>（官方接口）或 <c>compatible</c>（兼容接口）。</summary>
    public string Preset { get; init; } = "anthropic";

    /// <summary>是否允许上游位于本机或私网，以及是否允许使用明文 HTTP；默认只允许公网 HTTPS。</summary>
    public bool AllowPrivateNetwork { get; init; }

    /// <summary>出站 HTTP CONNECT 代理根地址；null 表示直连。</summary>
    public string? ProxyUrl { get; init; }

    /// <summary>检测和推荐给客户端使用的模型名；空字符串表示未填写，候选保存前要求填写。</summary>
    public string Model { get; init; } = "";

    /// <summary>候选检测方式：<c>messages</c>（最小消息调用）或 <c>claude-code</c>（启动真实 Claude Code 会话验证）。</summary>
    public string Diagnostic { get; init; } = "messages";

    /// <summary>
    /// 校验各枚举取值、凭据组合与模型名（最多 200 字符，仅字母数字和 <c>- _ . / :</c>），并规范化地址与代理，返回新的副本；原对象不变。
    /// 账户模式在官方预设下强制附加 OAuth beta，API 密钥模式不允许附加。任一不合法时抛出 <see cref="InvalidOperationException"/>。
    /// </summary>
    public UpstreamOptions Validated()
    {
        if (Diagnostic is not ("messages" or "claude-code")) throw new InvalidOperationException("请选择支持的调用检测方式。");
        if (Preset is not ("anthropic" or "compatible") || Mode is not ("oauth" or "api-key") || Authentication is not ("bearer" or "x-api-key")
            || Mode == "oauth" && Authentication != "bearer" || Mode == "api-key" && OAuthBeta)
            throw new InvalidOperationException("账户模式使用 Bearer；API 模式不可附加账户 OAuth beta。");
        if (Model.Length > 200 || Model.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or '/' or ':')))
            throw new InvalidOperationException("模型名称只能包含字母、数字和 - _ . / :，最多 200 字符。");
        var address = UpstreamAddress.Normalize(BaseUrl, AllowPrivateNetwork);
        var proxy = string.IsNullOrWhiteSpace(ProxyUrl) ? null : UpstreamAddress.Proxy(ProxyUrl).AbsoluteUri.TrimEnd('/');
        return this with { BaseUrl = address.Query.Length == 0 ? address.AbsoluteUri.TrimEnd('/') : address.AbsoluteUri, ProxyUrl = proxy,
            OAuthBeta = Mode == "oauth" && (Preset == "anthropic" || OAuthBeta) };
    }

    /// <summary>从旧版实例配置推导上游选项：沿用其认证方式与代理，账户模式使用 bearer 并附加 OAuth beta；结果未经 <see cref="Validated"/> 校验。</summary>
    public static UpstreamOptions Legacy(RelayConfiguration config, string baseUrl = "https://api.anthropic.com") => new()
    { Mode = config.AuthMode, Authentication = config.AuthMode == "oauth" ? "bearer" : "x-api-key",
        OAuthBeta = config.AuthMode == "oauth", BaseUrl = baseUrl, ProxyUrl = config.UpstreamProxy };
}
