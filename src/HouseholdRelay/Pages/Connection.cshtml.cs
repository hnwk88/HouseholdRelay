using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc;
using Application.Interfaces.Certificates;
using Application.Interfaces.Configuration;
using Application.Interfaces.Upstream;

namespace HouseholdRelay.Pages;

/// <summary>以已验证模型和公开地址生成说明，个人令牌永远使用占位符。</summary>
public sealed class ConnectionModel(IConfigurationService settings, IUpstreamService upstreams, ICertificateService certificates, IClientTrustPackage trustPackage) : PageModel
{
    /// <summary>设置里的对外公开地址，去掉末尾斜杠后用于拼接示例；未配置时为空串，示例改用 https://YOUR_RELAY_HOST 占位。</summary>
    public string RootUrl { get; private set; } = "";
    /// <summary>活动上游测试报告里实际验证通过的模型；只有报告允许激活时才填，否则为空，页面据此提示尚未验证。</summary>
    public string? VerifiedModel { get; private set; }
    /// <summary>推荐家人填写的模型名，取活动上游选项中配置的模型；仅在测试报告允许激活时给出，否则示例使用 YOUR_MODEL 占位。</summary>
    public string RecommendedModel { get; private set; } = "";
    /// <summary>测试报告列出的上游可用模型清单，仅在报告允许激活时展示。</summary>
    public string[] Models { get; private set; } = [];
    /// <summary>面向家人的 HTTPS 状态说明：未配置、已过期、私有 CA 需一次性信任、已导入可信证书四种情况之一。</summary>
    public string HttpsStatus { get; private set; } = "";
    /// <summary>PowerShell 环境变量设置示例（ANTHROPIC_BASE_URL 与 ANTHROPIC_AUTH_TOKEN）；令牌处固定为占位符，页面绝不回显任何真实个人令牌。</summary>
    public string Variables { get; private set; } = "";
    /// <summary>用 Authorization: Bearer 头调用 /v1/messages 的 curl 示例，令牌为占位符。</summary>
    public string BearerExample { get; private set; } = "";
    /// <summary>用 x-api-key 头调用 /v1/messages 的 curl 示例，令牌为占位符。</summary>
    public string ApiKeyExample { get; private set; } = "";
    /// <summary>活动上游是否经 Claude Code 实际调用验证（诊断方式为 claude-code）；此类上游可能拒绝通用 API 请求，为真时页面提醒 curl 示例只演示令牌写法。</summary>
    public bool ClaudeCodeOnly { get; private set; }
    /// <summary>私有 CA 根证书的 SHA-256 指纹，供家人安装信任前人工核对；未生成私有 CA 时为空。</summary>
    public string? AuthorityFingerprint { get; private set; }
    /// <summary>运行信任包内安装脚本的 PowerShell 命令，带上期望指纹，让脚本在指纹不符时拒绝安装。</summary>
    public string TrustCommand { get; private set; } = "";
    /// <summary>管理员打开连接说明页时调用（需已登录）：从设置、活动上游和证书信息组装上述展示内容，只读，不修改任何状态。</summary>
    public void OnGet()
    {
        RootUrl = settings.Read().Value.PublicUrl.TrimEnd('/');
        var active = upstreams.ReadActive();
        ClaudeCodeOnly = active.Options.Diagnostic == "claude-code";
        if (active.Report?.CanActivate == true)
        { VerifiedModel = active.Report.VerifiedModel; RecommendedModel = active.Options.Model; Models = active.Report.Models; }
        var certificate = certificates.ReadInfo();
        HttpsStatus = certificate is null ? "尚未配置 HTTPS。" : certificate.ValidUntil <= DateTimeOffset.UtcNow ? "证书已过期，请管理员更新。"
            : certificate.Mode == "private" ? "私有 CA 已配置；每台电脑完成下方一次性信任后即可使用。" : "已导入可信证书；请确认服务重启并从实际客户端检查信任。";
        AuthorityFingerprint = certificates.AuthorityFingerprint();
        TrustCommand = $".\\Install-RelayTrust.ps1 -ExpectedSha256 {AuthorityFingerprint}";
        var address = RootUrl.Length == 0 ? "https://YOUR_RELAY_HOST" : RootUrl;
        Variables = $"$env:ANTHROPIC_BASE_URL = '{address}'\n$env:ANTHROPIC_AUTH_TOKEN = '<你的个人中转令牌>'";
        var model = RecommendedModel.Length == 0 ? "YOUR_MODEL" : RecommendedModel;
        var body = $"{{\"model\":\"{model}\",\"max_tokens\":32,\"messages\":[{{\"role\":\"user\",\"content\":\"Hello\"}}]}}";
        string Example(string authentication) => $"curl --no-buffer '{address}/v1/messages' \\\n  -H '{authentication}' \\\n  -H 'anthropic-version: 2023-06-01' \\\n  -H 'content-type: application/json' \\\n  -d '{body}'";
        BearerExample = Example("Authorization: Bearer <你的个人中转令牌>");
        ApiKeyExample = Example("x-api-key: <你的个人中转令牌>");
    }
    /// <summary>下载客户端信任包（需已登录）：把私有 CA 公共证书 PEM 打成 zip 返回，只含公钥证书不含任何私钥；尚未生成私有 CA 时返回 404。</summary>
    public IActionResult OnGetTrustPackage()
    {
        var pem = certificates.ReadAuthorityPem();
        return pem is null ? NotFound() : File(trustPackage.Create(pem), "application/zip", "HouseholdRelay-client-trust.zip");
    }
}
