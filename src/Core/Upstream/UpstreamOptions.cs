using Core.Configuration;

namespace Core.Upstream;

/// <summary>一个候选版本的非秘密配置；出站代理是全局设置，不在这里。</summary>
public sealed record UpstreamOptions
{
    /// <summary>凭据类型：<c>oauth</c>（Claude 账户令牌）或 <c>api-key</c>（API 密钥）。</summary>
    public string Mode { get; init; } = "api-key";

    /// <summary>凭据放在哪个请求头：<c>bearer</c>（Authorization: Bearer）或 <c>x-api-key</c>；账户模式只能用 bearer。未手动指定时由获取模型和测试自动探测后写回。</summary>
    public string Authentication { get; init; } = "x-api-key";

    /// <summary>为 true 表示认证方式由管理员在高级设置里手动指定：探测只用这一种，结果不覆盖它。</summary>
    public bool AuthenticationManual { get; init; }

    /// <summary>上游 API 根地址；经 <see cref="Validated"/> 规范化后去掉末尾的 <c>/</c> 和 <c>/v1</c>。</summary>
    public string BaseUrl { get; init; } = "https://api.anthropic.com";

    /// <summary>是否在请求里附加账户 OAuth beta 标记；仅账户模式有效，官方预设下会被强制开启。</summary>
    public bool OAuthBeta { get; init; }

    /// <summary>上游预设：<c>anthropic</c>（官方接口）或 <c>compatible</c>（兼容接口）；经 <see cref="Validated"/> 时按地址主机重新推出，管理员不再选择。</summary>
    public string Preset { get; init; } = "anthropic";

    /// <summary>是否允许上游位于本机或私网，以及是否允许使用明文 HTTP；默认只允许公网 HTTPS。</summary>
    public bool AllowPrivateNetwork { get; init; }

    /// <summary>检测和推荐给客户端使用的模型名；空字符串表示还没选，测试前要求选定。</summary>
    public string Model { get; init; } = "";

    /// <summary>候选检测方式：<c>messages</c>（最小消息调用）或 <c>claude-code</c>（启动真实 Claude Code 会话验证）。未手动指定时由测试自动探测后写回。</summary>
    public string Diagnostic { get; init; } = "messages";

    /// <summary>为 true 表示检测方式由管理员在高级设置里手动指定：测试只用这一种，标准调用被拒时不自动改用 Claude Code。</summary>
    public bool DiagnosticManual { get; init; }

    /// <summary>
    /// 校验各枚举取值、凭据组合与模型名（最多 200 字符，仅字母数字和 <c>- _ . / :</c>），规范化地址并按地址主机推出预设，返回新的副本；原对象不变。
    /// 账户模式在官方预设下强制附加 OAuth beta，API 密钥模式不允许附加。任一不合法时抛出 <see cref="InvalidOperationException"/>。
    /// </summary>
    public UpstreamOptions Validated()
    {
        if (Diagnostic is not ("messages" or "claude-code")) throw new InvalidOperationException("请选择支持的调用检测方式。");
        if (Mode is not ("oauth" or "api-key") || Authentication is not ("bearer" or "x-api-key")
            || Mode == "oauth" && Authentication != "bearer" || Mode == "api-key" && OAuthBeta)
            throw new InvalidOperationException("账户模式使用 Bearer；API 模式不可附加账户 OAuth beta。");
        if (Model.Length > 200 || Model.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or '/' or ':')))
            throw new InvalidOperationException("模型名称只能包含字母、数字和 - _ . / :，最多 200 字符。");
        var address = UpstreamAddress.Normalize(BaseUrl, AllowPrivateNetwork);
        var preset = UpstreamDetection.Preset(address);
        return this with { BaseUrl = address.Query.Length == 0 ? address.AbsoluteUri.TrimEnd('/') : address.AbsoluteUri, Preset = preset,
            OAuthBeta = Mode == "oauth" && (preset == "anthropic" || OAuthBeta) };
    }

    /// <summary>从旧版实例配置推导上游选项：沿用其认证方式，账户模式使用 bearer 并附加 OAuth beta；旧代理由 v4 迁移改成全局设置，不进选项；结果未经 <see cref="Validated"/> 校验。</summary>
    public static UpstreamOptions Legacy(RelayConfiguration config, string baseUrl = "https://api.anthropic.com") => new()
    { Mode = config.AuthMode, Authentication = config.AuthMode == "oauth" ? "bearer" : "x-api-key",
        OAuthBeta = config.AuthMode == "oauth", BaseUrl = baseUrl };
}
