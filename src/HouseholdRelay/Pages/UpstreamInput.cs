using Core.Upstream;
using Core.Values;

namespace HouseholdRelay.Pages;

/// <summary>上游页「API 供应商」「登录官方账户」「导入登录文件」「贴账户访问令牌」四张表单的输入：管理员只填名称、地址、密钥（登录与导入时连密钥也不填），高级设置默认自动判断。</summary>
public sealed class UpstreamInput
{
    /// <summary>高级设置下拉框里「自动判断」的取值：只在页面上出现，不入库；选它时由获取模型和测试自动探测。</summary>
    public const string Automatic = "auto";

    /// <summary>把下拉框选中的线上名读成封闭取值；不认识时按原来的校验读句拒绝（伪造的表单值走到这里）。</summary>
    private static TValue Chosen<TValue>(string value, string rejection) where TValue : struct, Enum =>
        Wire<TValue>.TryParse(value, out var chosen) ? chosen : throw new InvalidOperationException(rejection);

    /// <summary>供应商名称，只做显示，例如「aitoll」「公司 sftoken」；必填，由服务校验。</summary>
    public string? Name { get; set; }
    /// <summary>上游 API 根地址；结尾带不带 /v1 都可以。</summary>
    public string? BaseUrl { get; set; }
    /// <summary>API 密钥或账户访问令牌；只在这一次提交里使用，处理后清空，页面永不回显。</summary>
    public string? Credential { get; set; }
    /// <summary>高级设置的认证方式：<c>auto</c>（自动判断）、<c>x-api-key</c> 或 <c>bearer</c>；不是 auto 时即手动指定。</summary>
    public string Authentication { get; set; } = Automatic;
    /// <summary>高级设置的检测方式：<c>auto</c>（自动判断）、<c>messages</c> 或 <c>claude-code</c>；不是 auto 时即手动指定。</summary>
    public string Diagnostic { get; set; } = Automatic;
    /// <summary>高级设置：允许连接本机或私网地址（包括 HTTP）。</summary>
    public bool AllowPrivateNetwork { get; set; }
    /// <summary>官方账户高级设置：兼容中转的账户模式也附加 OAuth beta；官方地址总会附加。</summary>
    public bool OAuthBeta { get; set; }

    /// <summary>按 API 供应商表单生成上游配置：自动判断时先按 x-api-key、标准 Messages 起步，由获取模型和测试改写；手动指定时打上手动标记。</summary>
    public UpstreamOptions ToApiOptions() => new()
    {
        Mode = UpstreamMode.ApiKey, BaseUrl = BaseUrl?.Trim() ?? "", AllowPrivateNetwork = AllowPrivateNetwork,
        Diagnostic = Diagnostic == Automatic ? DiagnosticMethod.Messages : Chosen<DiagnosticMethod>(Diagnostic, "请选择支持的调用检测方式。"), DiagnosticManual = Diagnostic != Automatic,
        Authentication = Authentication == Automatic ? AuthenticationScheme.XApiKey : Chosen<AuthenticationScheme>(Authentication, "账户模式使用 Bearer；API 模式不可附加账户 OAuth beta，也不能用官方账户登录。"),
        AuthenticationManual = Authentication != Automatic
    };

    /// <summary>按「登录官方账户」或「导入登录文件」表单生成上游配置：只取地址与私网开关，凭据来自登录或导入的文件，账户模式与 Bearer 由服务定死。</summary>
    public UpstreamOptions ToLoginOptions() => new() { Mode = UpstreamMode.OAuth, Authentication = AuthenticationScheme.Bearer, BaseUrl = BaseUrl?.Trim() ?? "", AllowPrivateNetwork = AllowPrivateNetwork, OfficialLogin = true };

    /// <summary>按官方账户表单生成上游配置：账户令牌固定用 Bearer，检测方式自动判断。</summary>
    public UpstreamOptions ToAccountOptions() => new()
    {
        Mode = UpstreamMode.OAuth, Authentication = AuthenticationScheme.Bearer, BaseUrl = BaseUrl?.Trim() ?? "", AllowPrivateNetwork = AllowPrivateNetwork, OAuthBeta = OAuthBeta
    };
}
