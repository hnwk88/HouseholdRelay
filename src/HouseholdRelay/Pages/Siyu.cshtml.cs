using Application.Contracts.Siyu;
using Application.Interfaces.Identity;
using Application.Interfaces.Siyu;
using Application.Interfaces.Upstream;
using Core.Configuration;
using Core.Siyu;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace HouseholdRelay.Pages;

/// <summary>思域会话一览：谁在用哪个官方账户、状态、租约、隧道与在途请求；管理员可以结束某个会话。没开思域时只读出怎么打开，不去构造会话服务。</summary>
public sealed class SiyuModel(RelayConfiguration config, Siyu.SiyuHostState host, Lazy<ISiyuSessionService> sessions, IIdentityService identities, IUpstreamService upstreams) : PageModel
{
    /// <summary>一行会话加上显示用的名字（名字只显示，关联走主键）。</summary>
    public sealed record Row(SiyuSessionSummary Session, string UserName, string AccountName);

    /// <summary>这次服务启动时思域是否打开（设置改了要重启才生效）。</summary>
    public bool Enabled => config.SiyuEnabled;

    /// <summary>思域代理在本进程起来了。</summary>
    public bool Active => host.Active;

    /// <summary>勾了思域但代理没起来时的原因。</summary>
    public string? Problem => host.Problem;

    /// <summary>思域代理端口。</summary>
    public int ProxyPort => config.SiyuProxyPort;

    /// <summary>旧接法的官方账户请求是否已关闭。</summary>
    public bool LegacyClosed => config.LegacyOfficialClosed;

    /// <summary>会话服务建不起来时的原因（例如对外地址没填）；正常为 null。</summary>
    public string? Unavailable { get; private set; }

    /// <summary>会话一览。</summary>
    public IReadOnlyList<Row> Rows { get; private set; } = [];

    /// <summary>操作后的提示。</summary>
    public string? Notice { get; private set; }

    /// <summary>打开一览（需已登录），只读。</summary>
    public Task OnGetAsync() => LoadAsync();

    /// <summary>结束一个会话（需已登录且通过防 CSRF 校验）。</summary>
    public async Task<IActionResult> OnPostCloseAsync(string sessionId)
    {
        if (Active && ModelState.IsValid && !string.IsNullOrEmpty(sessionId) && Service() is { } service)
        {
            service.CloseByAdministrator(sessionId);
            Notice = "已结束这个思域会话。正在进行的请求已取消，未确认完成的操作不会自动重发。";
        }
        await LoadAsync();
        return Page();
    }

    /// <summary>读一览并配上使用者显示名、账户名称。</summary>
    private async Task LoadAsync()
    {
        if (!Active || Service() is not { } service) return;
        var users = (await identities.ListAsync()).ToDictionary(user => user.Id, user => user.DisplayName);
        var accounts = (await upstreams.ListCandidatesAsync()).ToDictionary(account => account.Id, account => account.Name);
        Rows = [.. service.List().Select(session => new Row(session,
            users.GetValueOrDefault(session.Binding.UserId, $"使用者 {session.Binding.UserId}"),
            accounts.GetValueOrDefault(session.Binding.OfficialAccountId, $"官方账户 {session.Binding.OfficialAccountId}")))];
    }

    /// <summary>取会话服务；建不起来时记下原因、返回 null。</summary>
    private ISiyuSessionService? Service()
    {
        try { return sessions.Value; }
        // 对外地址没填等配置问题：读句本身就是下一步。
        catch (InvalidOperationException error) { Unavailable = error.Message; return null; }
    }

    /// <summary>状态的中文名。</summary>
    public static string StateName(SiyuSessionState state) => state switch
    {
        SiyuSessionState.Active => "使用中",
        SiyuSessionState.Closing => "正在结束",
        SiyuSessionState.Closed => "已结束",
        _ => "未知",
    };

    /// <summary>结束原因的中文读句；没有原因为空。</summary>
    public static string? CloseSentence(SiyuCloseReason? reason) => reason is { } value ? SiyuCloseReasons.Sentence(value) : null;
}
