using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Application.Interfaces.Upstream;
using Core.Upstream;

namespace HouseholdRelay.Pages;

/// <summary>保存不可变候选，单独确认诊断费用，再显式激活通过检测的版本。</summary>
public sealed class UpstreamModel(IUpstreamService upstreams, IUpstreamPool upstreamPool) : PageModel
{
    /// <summary>表单回传的上游非秘密配置（模式、地址、模型等）；默认回显最近候选的配置，没有候选时回显活动上游的配置。</summary>
    [BindProperty] public UpstreamOptions Input { get; set; } = new();
    /// <summary>新填写的上游凭据；只在保存候选时使用，处理后无论成败都清空，页面永不回显已保存的凭据。</summary>
    [BindProperty] public string Credential { get; set; } = "";
    /// <summary>打开页面时的活动上游版本号；保存候选时若活动版本已变化则拒绝，防止基于过期配置建候选。</summary>
    [BindProperty] public long Version { get; set; }
    /// <summary>当前活动上游的不可变快照；每个处理程序渲染前由 <see cref="Load"/> 填充，页面只使用其中的非秘密字段。</summary>
    public UpstreamSnapshot Active { get; private set; } = null!;
    /// <summary>本次操作涉及的候选，未指定时取最近保存的候选；只含可展示的信息，不含秘密。</summary>
    public UpstreamCandidate? Candidate { get; private set; }
    /// <summary>操作成功后的提示，例如候选已保存、检测结果或已激活。</summary>
    public string? Notice { get; private set; }
    /// <summary>活动上游的运行状态文字：未配置、凭据失效、冷却中（含剩余秒数）、已通过检测或尚未检测。</summary>
    public string RuntimeStatus { get; private set; } = "";
    /// <summary>打开上游设置页（需已登录）：读取活动上游与最近候选并回显其配置，只读。</summary>
    public void OnGet()
    {
        Load(); Input = Candidate?.Options ?? Active.Options; Version = Active.Version;
    }
    /// <summary>保存候选（需已登录且通过防 CSRF 校验）：OAuth 模式强制使用 bearer 认证，非 OAuth 模式关闭 OAuth beta；校验并加密保存为不可变候选，不改变活动上游，之后须单独测试、再显式激活。</summary>
    public IActionResult OnPostSave()
    {
        if (ModelState.IsValid)
        {
            try
            {
                Input = Input with { Authentication = Input.Mode == "oauth" ? "bearer" : Input.Authentication, OAuthBeta = Input.Mode == "oauth" && Input.OAuthBeta };
                var id = upstreams.SaveCandidate(Version, Input, Credential);
                Candidate = upstreams.ReadCandidate(id); Notice = "候选已保存，尚未改变活动上游。请确认后测试最小调用。";
            }
            // 配置不合法或活动版本已变化时只显示表单错误，不保存候选；凭据随后在下面统一清空。
            catch (InvalidOperationException error) { ModelState.AddModelError("", error.Message); }
        }
        ClearCredential(); Load(); return Page();
    }
    /// <summary>测试候选（需已登录且通过防 CSRF 校验）：须勾选确认后才对已保存的候选做一次最小模型调用，因为这会产生真实的上游费用；浏览器断开时经 RequestAborted 取消对上游的调用。</summary>
    public async Task<IActionResult> OnPostTestAsync(string candidateId, bool confirmed)
    {
        // 检测表单不携带凭据或配置；只按候选主键加载已经保存的完整版本。
        ModelState.Clear();
        try { Notice = (await upstreams.TestCandidate(candidateId, confirmed, HttpContext.RequestAborted)).Message; }
        // 未确认费用、候选不存在等业务拒绝只作为表单错误显示，不改动活动上游。
        catch (InvalidOperationException error) { ModelState.AddModelError("", error.Message); }
        Load(); Input = Candidate?.Options ?? Active.Options; Version = Active.Version; return Page();
    }
    /// <summary>激活候选（需已登录且通过防 CSRF 校验）：把检测通过的候选原子切换为新的活动版本，后续请求立即使用新配置，无需重启；已在途的请求继续使用旧版本连接，个人令牌和中转地址不变。</summary>
    public IActionResult OnPostActivate(string candidateId)
    {
        ModelState.Clear();
        try { upstreams.Activate(candidateId); Notice = "新上游已激活，后续请求使用新配置；个人令牌和中转地址无需更换。"; }
        // 候选未通过检测或已失效时拒绝激活，只显示表单错误，活动上游保持原样。
        catch (InvalidOperationException error) { ModelState.AddModelError("", error.Message); }
        Load(); Input = Candidate?.Options ?? Active.Options; Version = Active.Version; return Page();
    }
    /// <summary>清空凭据绑定值并移出模型状态，确保重新渲染的页面不会把刚提交的凭据回填到表单。</summary>
    private void ClearCredential() { Credential = ""; ModelState.Remove(nameof(Credential)); }
    /// <summary>重新读取活动上游、最近候选（未指定时）和上游池运行状态，生成 <see cref="RuntimeStatus"/>。</summary>
    private void Load()
    {
        Active = upstreams.ReadActive(); Candidate ??= upstreams.LatestCandidate();
        var state = upstreamPool.Status(Active.Version);
        RuntimeStatus = !Active.Configured ? "未配置" : state.Paused ? "凭据失效，需重新接入" : state.CooldownSeconds > 0
            ? $"上游冷却中，约 {state.CooldownSeconds} 秒后重试" : Active.Report?.CanActivate == true ? "已通过最小调用检测" : "已迁移凭据，调用尚未检测";
    }
}
