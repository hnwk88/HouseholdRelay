using Microsoft.AspNetCore.Mvc.RazorPages;
using Application.Interfaces.Certificates;
using Application.Interfaces.Configuration;
using Application.Interfaces.Identity;
using Application.Interfaces.Upstream;

namespace HouseholdRelay.Pages;

/// <summary>管理首页：使用者数、上游与 HTTPS 状态概览。</summary>
public sealed class IndexModel(IConfigurationService settings, IIdentityService identities, IUpstreamService upstreams, ICertificateService certificates, IUpstreamPool upstreamPool, IRelayInstance instance) : PageModel
{
    /// <summary>最近一次数据库升级的读句（版本、升级时刻、备份位置、迁移另存条数）；从没升级过时为空串，页面不显示这一格。</summary>
    public string UpgradeText { get; private set; } = "";
    /// <summary>已登记的使用者（家庭成员）人数，含已停用的。</summary>
    public int UserCount { get; private set; }
    /// <summary>活动上游是否已保存凭据；只反映有无，不读取凭据内容。</summary>
    public bool CredentialConfigured { get; private set; }
    /// <summary>是否已保存服务器证书；只反映有无，与证书是否已在本进程启用无关。</summary>
    public bool CertificateConfigured { get; private set; }
    /// <summary>设置中的对外公开地址，原样展示。</summary>
    public string PublicUrl { get; private set; } = "";
    /// <summary>上游状态文字：已导出搬走、未配置、凭据失效需重新接入、冷却中、最小调用已验证、有凭据但未检测，按此优先级取第一个成立的。</summary>
    public string UpstreamStatus { get; private set; } = "尚未配置";
    /// <summary>当前生效上游的供应商名称；没有生效上游时为「尚未选择」，生效的是旧版迁移来的未命名上游时为「未命名」。</summary>
    public string UpstreamName { get; private set; } = "尚未选择";
    /// <summary>此刻实际走的是哪家、哪家暂避到几点、为什么；没有生效上游时为空。</summary>
    public string RoutingText { get; private set; } = "";
    /// <summary>证书状态文字：未配置、已过期、私有 CA 需客户端信任或已导入可信证书。</summary>
    public string CertificateStatus { get; private set; } = "尚未配置 HTTPS";
    /// <summary>打开管理首页（需已登录）：汇总使用者数、上游池运行状态和证书状态，只读。</summary>
    public async Task OnGetAsync()
    {
        UserCount = (await identities.ListAsync()).Count; CredentialConfigured = await settings.HasCredentialAsync();
        CertificateConfigured = await settings.HasCertificateAsync(); PublicUrl = (await settings.ReadAsync()).Value.PublicUrl;
        var active = await upstreams.ReadOverviewAsync(); var state = upstreamPool.Status(active.Version); var saved = await upstreams.ListCandidatesAsync();
        // 在用的那家已导出搬走：照上游页同一句读出请求此刻怎么走和下一步，不读成「尚未配置」。
        var moved = saved.FirstOrDefault(item => item.Id == active.UpstreamCandidateId) is { ExportedAt: not null } current ? current : null;
        UpstreamName = moved?.Name ?? (!active.Configured ? "尚未选择" : active.Name ?? "未命名");
        // 运行状态与上游页读同一句（UpstreamOverview.RuntimeText），不各写一份。
        UpstreamStatus = active.RuntimeText(moved is not null, state.Paused, state.CooldownSeconds);
        RoutingText = moved is not null ? UpstreamModel.MovedSentence(moved.Name, active.Failover)
            : active.Configured ? UpstreamModel.RoutingSentence(upstreamPool, active, saved) : "";
        UpgradeText = instance.LastUpgrade is { } upgrade ? $"{upgrade.At.ToLocalTime():yyyy-MM-dd HH:mm}：{upgrade.Sentence()}" : "";
        // 30 天内到期时读到期提示（与 HTTPS 页、连接说明同一句，见 CertificateInfo.ExpiryWarning），其余照旧。
        var certificate = await certificates.ReadInfoAsync(); var now = DateTimeOffset.UtcNow;
        CertificateStatus = certificate is null ? "尚未配置 HTTPS" : certificate.Expired(now) ? "证书已过期"
            : certificate.ExpiryWarning(now, await certificates.CanReissueAsync()) ?? (certificate.Mode == "private" ? "私有 CA · 客户端安装一次信任" : "已导入可信证书");
    }
}
