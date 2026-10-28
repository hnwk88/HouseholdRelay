using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace HouseholdRelay.Pages;

/// <summary>保存不可变候选，单独确认诊断费用，再显式激活通过检测的版本。</summary>
public sealed class UpstreamModel(UpstreamService upstreams, RelayServer relay) : PageModel
{
    [BindProperty] public UpstreamOptions Input { get; set; } = new();
    [BindProperty] public string Credential { get; set; } = "";
    [BindProperty] public long Version { get; set; }
    public UpstreamSnapshot Active { get; private set; } = null!;
    public UpstreamCandidate? Candidate { get; private set; }
    public string? Notice { get; private set; }
    public string RuntimeStatus { get; private set; } = "";
    public void OnGet()
    {
        Load(); Input = Candidate?.Options ?? Active.Options; Version = Active.Version;
    }
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
            catch (InvalidOperationException error) { ModelState.AddModelError("", error.Message); }
        }
        ClearCredential(); Load(); return Page();
    }
    public async Task<IActionResult> OnPostTestAsync(string candidateId, bool confirmed)
    {
        // 检测表单不携带凭据或配置；只按候选主键加载已经保存的完整版本。
        ModelState.Clear();
        try { Notice = (await upstreams.TestCandidate(candidateId, confirmed, HttpContext.RequestAborted)).Message; }
        catch (InvalidOperationException error) { ModelState.AddModelError("", error.Message); }
        Load(); Input = Candidate?.Options ?? Active.Options; Version = Active.Version; return Page();
    }
    public IActionResult OnPostActivate(string candidateId)
    {
        ModelState.Clear();
        try { upstreams.Activate(candidateId); Notice = "新上游已激活，后续请求使用新配置；个人令牌和中转地址无需更换。"; }
        catch (InvalidOperationException error) { ModelState.AddModelError("", error.Message); }
        Load(); Input = Candidate?.Options ?? Active.Options; Version = Active.Version; return Page();
    }
    private void ClearCredential() { Credential = ""; ModelState.Remove(nameof(Credential)); }
    private void Load()
    {
        Active = upstreams.ReadActive(); Candidate ??= upstreams.LatestCandidate();
        var state = relay.Upstreams.Status(Active.Version);
        RuntimeStatus = !Active.Configured ? "未配置" : state.Paused ? "凭据失效，需重新接入" : state.CooldownSeconds > 0
            ? $"上游冷却中，约 {state.CooldownSeconds} 秒后重试" : Active.Report?.CanActivate == true ? "已通过最小调用检测" : "已迁移凭据，调用尚未检测";
    }
}
