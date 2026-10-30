using Core.Upstream;

namespace HouseholdRelay.Pages;

/// <summary>上游页「API 供应商」「登录官方账户」「导入登录文件」「贴账户访问令牌」四张表单的输入：管理员只填名称、地址、密钥（登录与导入时连密钥也不填），高级设置默认自动判断。</summary>
public sealed class UpstreamInput
{
    /// <summary>供应商名称，只做显示，例如「aitoll」「公司 sftoken」；必填，由服务校验。</summary>
    public string? Name { get; set; }
    /// <summary>上游 API 根地址；结尾带不带 /v1 都可以。</summary>
    public string? BaseUrl { get; set; }
    /// <summary>API 密钥或账户访问令牌；只在这一次提交里使用，处理后清空，页面永不回显。</summary>
    public string? Credential { get; set; }
    /// <summary>高级设置的认证方式：<c>auto</c>（自动判断）、<c>x-api-key</c> 或 <c>bearer</c>；不是 auto 时即手动指定。</summary>
    public string Authentication { get; set; } = "auto";
    /// <summary>高级设置的检测方式：<c>auto</c>（自动判断）、<c>messages</c> 或 <c>claude-code</c>；不是 auto 时即手动指定。</summary>
    public string Diagnostic { get; set; } = "auto";
    /// <summary>高级设置：允许连接本机或私网地址（包括 HTTP）。</summary>
    public bool AllowPrivateNetwork { get; set; }
    /// <summary>官方账户高级设置：兼容中转的账户模式也附加 OAuth beta；官方地址总会附加。</summary>
    public bool OAuthBeta { get; set; }

    /// <summary>按 API 供应商表单生成上游配置：自动判断时先按 x-api-key、标准 Messages 起步，由获取模型和测试改写；手动指定时打上手动标记。</summary>
    public UpstreamOptions ToApiOptions() => new()
    {
        Mode = "api-key", BaseUrl = BaseUrl?.Trim() ?? "", AllowPrivateNetwork = AllowPrivateNetwork,
        Authentication = Authentication == "auto" ? "x-api-key" : Authentication, AuthenticationManual = Authentication != "auto",
        Diagnostic = Diagnostic == "auto" ? "messages" : Diagnostic, DiagnosticManual = Diagnostic != "auto"
    };

    /// <summary>按「登录官方账户」或「导入登录文件」表单生成上游配置：只取地址与私网开关，凭据来自登录或导入的文件，账户模式与 Bearer 由服务定死。</summary>
    public UpstreamOptions ToLoginOptions() => new() { Mode = "oauth", Authentication = "bearer", BaseUrl = BaseUrl?.Trim() ?? "", AllowPrivateNetwork = AllowPrivateNetwork, OfficialLogin = true };

    /// <summary>按官方账户表单生成上游配置：账户令牌固定用 Bearer，检测方式自动判断。</summary>
    public UpstreamOptions ToAccountOptions() => new()
    {
        Mode = "oauth", Authentication = "bearer", BaseUrl = BaseUrl?.Trim() ?? "", AllowPrivateNetwork = AllowPrivateNetwork, OAuthBeta = OAuthBeta
    };
}
