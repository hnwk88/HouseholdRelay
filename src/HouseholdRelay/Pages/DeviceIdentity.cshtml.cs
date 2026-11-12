using Application.Interfaces.Configuration;
using Application.Interfaces.Relay;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace HouseholdRelay.Pages;

/// <summary>管理员设备维护与临时脱敏取证，只开放确认更换和继续原操作。</summary>
public sealed class DeviceIdentityModel(ISiteIdentityService identity, IProtocolDiagnostics diagnostics) : PageModel
{
    /// <summary>页面生成的稳定操作编号，双击和重试沿用。</summary>
    [BindProperty] public string OperationId { get; set; } = "";
    /// <summary>页面看到的身份版本，用于拒绝过期页面。</summary>
    [BindProperty] public long Version { get; set; }
    /// <summary>管理员明确确认全部官方账户的临时暂停。</summary>
    [BindProperty] public bool Confirmed { get; set; }
    /// <summary>不含设备原值的当前状态。</summary>
    public string Progress => identity.Status;
    /// <summary>是否有需要继续的持久更换。</summary>
    public bool Pending => identity.Current.Pending;
    /// <summary>是否已完成准备。</summary>
    public bool Available => identity.Available;
    /// <summary>临时取证状态，不展示日志明文。</summary>
    public string DiagnosticStatus => diagnostics.Status;
    /// <summary>本次表单结果。</summary>
    public string? Notice { get; private set; }

    /// <summary>只读当前版本与操作号，不读取上游凭据或触发续期。</summary>
    public void OnGet() => Load();

    /// <summary>管理员会话与防 CSRF 通过后才调用单一维护入口。</summary>
    public async Task<IActionResult> OnPostRefreshAsync(CancellationToken cancellation)
    {
        if (!Confirmed) ModelState.AddModelError(nameof(Confirmed), "请先确认暂停范围和更换影响。");
        if (ModelState.IsValid)
        {
            try
            {
                var result = await identity.Refresh(OperationId, Version, cancellation);
                Notice = result.Current.Pending || !identity.Available ? identity.Status : "这次设备维护已完成。重复提交不会再次更换，客户端个人令牌可以照常使用。";
            }
            // 服务的异常是固定的管理员读句，不能把底层文件或网络异常显示出来。
            catch (InvalidOperationException error) { ModelState.AddModelError("", error.Message); }
        }
        return Page();
    }

    /// <summary>恢复当前未完成的操作，初次准备失败时重新准备已有编号。</summary>
    public async Task<IActionResult> OnPostResumeAsync(CancellationToken cancellation)
    {
        try
        {
            if (!identity.Current.Pending) await identity.Prepare(cancellation);
            else await identity.Resume(OperationId, cancellation);
            Notice = identity.Status;
        }
        // 持久目标保持原样，页面读出继续步骤。
        catch (InvalidOperationException error) { ModelState.AddModelError("", error.Message); }
        Load(); return Page();
    }

    /// <summary>开启短时取证，不生成试请求，也不运行登录或续期。</summary>
    public IActionResult OnPostStartDiagnostics() { diagnostics.Start(); Load(); return Page(); }
    /// <summary>立即关闭取证。</summary>
    public IActionResult OnPostStopDiagnostics() { diagnostics.Stop(); Load(); return Page(); }
    /// <summary>设置表单版本和稳定操作号，设备值不交给页面。</summary>
    private void Load()
    {
        var current = identity.Current;
        Version = current.Version; OperationId = current.Pending ? current.OperationId! : Guid.NewGuid().ToString("N");
    }
}
