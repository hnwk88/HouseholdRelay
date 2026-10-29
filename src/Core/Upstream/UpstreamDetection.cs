namespace Core.Upstream;

/// <summary>上游自动探测的规则：预设按地址主机推、认证方式按固定顺序尝试，并把探测结果读成管理员听得懂的一句话。</summary>
public static class UpstreamDetection
{
    /// <summary>官方 Anthropic 接口的主机名；只有这个主机按官方预设处理，其余一律按兼容中转处理。</summary>
    public const string OfficialHost = "api.anthropic.com";

    /// <summary>按规范化后的上游地址推出预设：主机是 <see cref="OfficialHost"/> 时为 <c>anthropic</c>，否则为 <c>compatible</c>。</summary>
    public static string Preset(Uri address) => address.Host.Equals(OfficialHost, StringComparison.OrdinalIgnoreCase) ? "anthropic" : "compatible";

    /// <summary>认证方式的尝试顺序：账户模式只有 Bearer；高级设置手动指定时只试那一种；其余先 x-api-key 后 Bearer。</summary>
    public static string[] AuthenticationOrder(UpstreamOptions options) =>
        options.Mode == "oauth" ? ["bearer"] : options.AuthenticationManual ? [options.Authentication] : ["x-api-key", "bearer"];

    /// <summary>从 <paramref name="preferred"/> 开始截取尝试顺序：模型目录已接受某种认证时，测试从它开始，只在被拒后才换排在后面的那种。</summary>
    public static string[] StartingFrom(string[] order, string preferred)
    {
        var index = Array.IndexOf(order, preferred);
        return index < 0 ? order : order[index..];
    }

    /// <summary>认证方式的朗读名：<c>bearer</c> 读作 Bearer，其余读作 x-api-key。</summary>
    public static string AuthenticationLabel(string authentication) => authentication == "bearer" ? "Bearer" : "x-api-key";

    /// <summary>检测方式的朗读名：<c>claude-code</c> 读作 Claude Code，其余读作标准 Messages。</summary>
    public static string DiagnosticLabel(string diagnostic) => diagnostic == "claude-code" ? "Claude Code" : "标准 Messages";

    /// <summary>测试通过时读出的一句话，例如「认证用 Bearer，按 Claude Code 方式通过，模型 claude-opus-5-5。」；手动指定的项目后面注明「（手动指定）」。</summary>
    public static string Passed(UpstreamOptions used, string model) =>
        $"认证用 {AuthenticationLabel(used.Authentication)}{Manual(used.AuthenticationManual)}，{(used.Diagnostic == "claude-code" ? "按 Claude Code" : "按标准 Messages")}{Manual(used.DiagnosticManual)} 方式通过，模型 {model}。";

    /// <summary>所有可试的认证方式都被拒绝时读出的原因与下一步；账户令牌、手动指定和自动探测三种情况分别说明。</summary>
    public static string Rejected(UpstreamOptions options) =>
        options.Mode == "oauth" ? "账户令牌被上游拒绝。请重新取得账户令牌后再填写。"
        : options.AuthenticationManual ? $"密钥被上游拒绝（认证方式手动指定为 {AuthenticationLabel(options.Authentication)}）。请核对密钥，或在高级设置里改回自动判断。"
        : "密钥被上游拒绝，两种认证方式（x-api-key、Bearer）都试过。请核对密钥后重新填写。";

    /// <summary>手动指定时追加的说明文字；自动探测时为空。</summary>
    private static string Manual(bool manual) => manual ? "（手动指定）" : "";
}
