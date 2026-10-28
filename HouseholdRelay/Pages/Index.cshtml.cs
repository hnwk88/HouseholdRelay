using Microsoft.AspNetCore.Mvc.RazorPages;

namespace HouseholdRelay.Pages;

public sealed class IndexModel(ConfigurationService settings, IdentityService identities, UpstreamService upstreams, CertificateService certificates, RelayServer relay) : PageModel
{
    public int UserCount { get; private set; }
    public bool CredentialConfigured { get; private set; }
    public bool CertificateConfigured { get; private set; }
    public string PublicUrl { get; private set; } = "";
    public string UpstreamStatus { get; private set; } = "尚未配置";
    public string CertificateStatus { get; private set; } = "尚未配置 HTTPS";
    public void OnGet()
    {
        UserCount = identities.List().Count; CredentialConfigured = settings.HasCredential;
        CertificateConfigured = settings.HasCertificate; PublicUrl = settings.Read().Value.PublicUrl;
        var active = upstreams.ReadActive(); var state = relay.Upstreams.Status(active.Version);
        UpstreamStatus = !active.Configured ? "尚未配置" : state.Paused ? "凭据失效，需重新接入" : state.CooldownSeconds > 0 ? "上游冷却中"
            : active.Report?.CanActivate == true ? "最小模型调用已验证" : "已有凭据 · 调用未检测";
        var certificate = certificates.ReadInfo(); CertificateStatus = certificate is not null
            ? certificate.ValidUntil > DateTimeOffset.UtcNow ? certificate.Mode == "private" ? "私有 CA · 客户端安装一次信任" : "已导入可信证书" : "证书已过期"
            : "尚未配置 HTTPS";
    }
}
