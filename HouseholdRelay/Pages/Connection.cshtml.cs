using Microsoft.AspNetCore.Mvc.RazorPages;

namespace HouseholdRelay.Pages;

/// <summary>以已验证模型和公开地址生成说明，个人令牌永远使用占位符。</summary>
public sealed class ConnectionModel(ConfigurationService settings, UpstreamService upstreams, CertificateService certificates) : PageModel
{
    public string RootUrl { get; private set; } = "";
    public string? VerifiedModel { get; private set; }
    public string RecommendedModel { get; private set; } = "";
    public string[] Models { get; private set; } = [];
    public string HttpsStatus { get; private set; } = "";
    public string Variables { get; private set; } = "";
    public string BearerExample { get; private set; } = "";
    public string ApiKeyExample { get; private set; } = "";
    public bool ClaudeCodeOnly { get; private set; }
    public void OnGet()
    {
        RootUrl = settings.Read().Value.PublicUrl.TrimEnd('/');
        var active = upstreams.ReadActive();
        ClaudeCodeOnly = active.Options.Diagnostic == "claude-code";
        if (active.Report?.CanActivate == true)
        { VerifiedModel = active.Report.VerifiedModel; RecommendedModel = active.Options.Model; Models = active.Report.Models; }
        var certificate = certificates.ReadInfo();
        HttpsStatus = certificate?.Mode == "trusted" && certificate.ValidUntil > DateTimeOffset.UtcNow ? "已导入可信证书；请确认服务重启和公网映射已完成。"
            : settings.HasCertificate ? "当前为旧版私有 CA 或证书已过期，尚不满足直接信任的连接条件。" : "尚未配置可信 HTTPS。";
        var address = RootUrl.Length == 0 ? "https://YOUR_RELAY_HOST" : RootUrl;
        Variables = $"$env:ANTHROPIC_BASE_URL = '{address}'\n$env:ANTHROPIC_AUTH_TOKEN = '<你的个人中转令牌>'";
        var model = RecommendedModel.Length == 0 ? "YOUR_MODEL" : RecommendedModel;
        var body = $"{{\"model\":\"{model}\",\"max_tokens\":32,\"messages\":[{{\"role\":\"user\",\"content\":\"Hello\"}}]}}";
        string Example(string authentication) => $"curl --no-buffer '{address}/v1/messages' \\\n  -H '{authentication}' \\\n  -H 'anthropic-version: 2023-06-01' \\\n  -H 'content-type: application/json' \\\n  -d '{body}'";
        BearerExample = Example("Authorization: Bearer <你的个人中转令牌>");
        ApiKeyExample = Example("x-api-key: <你的个人中转令牌>");
    }
}
