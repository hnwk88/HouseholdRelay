using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Application.Interfaces.Upstream;
using Core.Upstream;

namespace HouseholdRelay.Pages;

/// <summary>上游接入页：起名字、填地址、贴密钥、获取模型、按写明费用的按钮测试，再按名称切换；认证方式与检测方式由程序探测。</summary>
public sealed class UpstreamModel(IUpstreamService upstreams, IUpstreamPool upstreamPool) : PageModel
{
    /// <summary>「API 供应商」表单。</summary>
    [BindProperty] public UpstreamInput Api { get; set; } = new();
    /// <summary>「官方账户」表单；地址默认官方接口。</summary>
    [BindProperty] public UpstreamInput Account { get; set; } = new() { BaseUrl = "https://" + UpstreamDetection.OfficialHost };
    /// <summary>打开页面时的活动上游版本号；保存与切换时若活动版本已变化则拒绝，防止基于过期页面操作。</summary>
    [BindProperty] public long Version { get; set; }
    /// <summary>当前活动上游的不可变快照；页面只使用其中的非秘密字段。</summary>
    public UpstreamSnapshot Active { get; private set; } = null!;
    /// <summary>全部已保存的上游，按创建先后排列。</summary>
    public IReadOnlyList<UpstreamCandidate> Saved { get; private set; } = [];
    /// <summary>正在设置的上游（刚获取模型、或在列表里点了「选择模型并测试」）；没有时不显示第二步。</summary>
    public UpstreamCandidate? Working { get; private set; }
    /// <summary>操作成功后的提示，例如获取模型的结果、测试读句或已切换。</summary>
    public string? Notice { get; private set; }
    /// <summary>活动上游的运行状态文字：未配置、凭据失效、冷却中（含剩余秒数）、已通过检测或尚未检测。</summary>
    public string RuntimeStatus { get; private set; } = "";

    /// <summary>打开上游页（需已登录）：<paramref name="candidateId"/> 指定时把它作为正在设置的上游，否则取最近一个还没测通过的；只读。</summary>
    public void OnGet(string? candidateId) => Load(candidateId);

    /// <summary>API 供应商：加密保存名称、地址、密钥后立刻获取模型（不花额度）；密钥只在这一次提交里出现，处理后清空。</summary>
    public Task<IActionResult> OnPostDiscoverApiAsync() => Discover(Api, Api.ToApiOptions());

    /// <summary>官方账户：加密保存名称、地址、账户令牌后立刻获取模型（不花额度）。</summary>
    public Task<IActionResult> OnPostDiscoverAccountAsync() => Discover(Account, Account.ToAccountOptions());

    /// <summary>
    /// 测试（需已登录且通过防 CSRF 校验）：按钮文字已写明会消耗少量上游额度，按下即视为同意；先按需改选模型，再对已保存的上游做一次最小调用。
    /// 浏览器断开时经 RequestAborted 取消对上游的调用；测试不会切换当前上游。
    /// </summary>
    public async Task<IActionResult> OnPostTestAsync(string candidateId, string? model)
    {
        ModelState.Clear();
        try
        {
            if (model is not null) upstreams.ChooseModel(candidateId, model);
            Notice = (await upstreams.TestCandidate(candidateId, true, HttpContext.RequestAborted)).Message;
        }
        // 未选模型、模型名不合规则、上游不存在或正在检测：只作为表单错误读出，不改动活动上游。
        catch (InvalidOperationException error) { ModelState.AddModelError("", error.Message); }
        Load(candidateId); return Page();
    }

    /// <summary>切换（需已登录且通过防 CSRF 校验）：把测试通过的上游原子切换为活动版本，核对的是本页打开时看到的版本；在途请求继续用旧版本，个人令牌和中转地址不变。</summary>
    public IActionResult OnPostActivate(string candidateId)
    {
        ModelState.Clear();
        try
        {
            upstreams.Activate(candidateId, Version);
            Notice = $"已切换到「{upstreams.ReadCandidate(candidateId).Name}」。后续请求使用它；个人令牌和中转地址不用换。";
        }
        // 没测通过或活动版本已被别处改动：读出原因，活动上游保持原样。
        catch (InvalidOperationException error) { ModelState.AddModelError("", error.Message); }
        Load(null); return Page();
    }

    /// <summary>改名（需已登录且通过防 CSRF 校验）：只改显示名称，切换和用量都按主键关联，不受影响。</summary>
    public IActionResult OnPostRename(string candidateId, string? newName)
    {
        ModelState.Clear();
        try { upstreams.Rename(candidateId, newName ?? ""); Notice = $"已改名为「{upstreams.ReadCandidate(candidateId).Name}」。"; }
        // 名称为空、过长或上游不存在：读出原因，名称保持原样。
        catch (InvalidOperationException error) { ModelState.AddModelError("", error.Message); }
        Load(null); return Page();
    }

    /// <summary>两张表单共用的保存加获取模型：成功后清空表单并把它作为正在设置的上游；任一步拒绝都读出原因。密钥无论成败都清空。</summary>
    private async Task<IActionResult> Discover(UpstreamInput input, UpstreamOptions options)
    {
        string? working = null;
        try
        {
            working = upstreams.SaveCandidate(Version, options, input.Credential ?? "", input.Name ?? "");
            var catalog = await upstreams.DiscoverModels(working, HttpContext.RequestAborted);
            Notice = $"已保存「{upstreams.ReadCandidate(working).Name}」。{catalog.Message}";
            ModelState.Clear(); Api = new(); Account = new() { BaseUrl = "https://" + UpstreamDetection.OfficialHost };
        }
        // 名称、地址、密钥不合规则或活动版本已变化：只作为表单错误读出；已保存但获取模型被拒的情况由目录读句说明，不走这里。
        catch (InvalidOperationException error) { ModelState.AddModelError("", error.Message); }
        var prefix = ReferenceEquals(input, Api) ? nameof(Api) : nameof(Account);
        // 误把密钥贴进名称或地址时，这两栏也不能原样回填，否则密钥会出现在页面上。
        if (input.Credential is { Length: >= 8 } secret)
        {
            if (input.Name?.Contains(secret, StringComparison.Ordinal) == true) { input.Name = null; ModelState.Remove(prefix + "." + nameof(UpstreamInput.Name)); }
            if (input.BaseUrl?.Contains(secret, StringComparison.Ordinal) == true) { input.BaseUrl = null; ModelState.Remove(prefix + "." + nameof(UpstreamInput.BaseUrl)); }
        }
        input.Credential = null; ModelState.Remove(prefix + "." + nameof(UpstreamInput.Credential));
        Load(working); return Page();
    }

    /// <summary>重新读取活动上游、已保存列表、正在设置的上游和上游池运行状态，并把版本号更新为最新。</summary>
    private void Load(string? workingId)
    {
        Active = upstreams.ReadActive(); Saved = upstreams.ListCandidates(); Version = Active.Version; ModelState.Remove(nameof(Version));
        // 其他按钮的提交不带官方账户表单字段，绑定会给出空地址；补回官方地址，避免读屏读到一个空的必填框。
        if (string.IsNullOrWhiteSpace(Account.BaseUrl)) Account.BaseUrl = "https://" + UpstreamDetection.OfficialHost;
        Working = workingId is null ? Saved.LastOrDefault(item => item.State != "passed") : Saved.FirstOrDefault(item => item.Id == workingId);
        var state = upstreamPool.Status(Active.Version);
        RuntimeStatus = !Active.Configured ? "未配置" : state.Paused ? "凭据失效，需重新接入" : state.CooldownSeconds > 0
            ? $"上游冷却中，约 {state.CooldownSeconds} 秒后重试" : Active.Report?.CanActivate == true ? "已通过最小调用检测" : "已迁移凭据，调用尚未检测";
    }

    /// <summary>列表与第二步里「认证方式」的读法：手动指定或已被上游确认时读出方式，否则读「尚未确认」。</summary>
    public static string AuthenticationText(UpstreamCandidate item) =>
        item.Options.AuthenticationManual || item.Options.Mode == "oauth" || item.Report?.Authentication == "passed"
            ? UpstreamDetection.AuthenticationLabel(item.Options.Authentication) + (item.Options.AuthenticationManual ? "（手动指定）" : "")
            : "尚未确认";

    /// <summary>列表里「最近一次检测」的读法：通过、未通过（附原因）、正在检测或尚未测试。</summary>
    public static string ResultText(UpstreamCandidate item) => item.State switch
    {
        "passed" => $"测试通过（{item.Report?.CheckedAt.ToLocalTime():yyyy-MM-dd HH:mm}）。{item.Report?.Message}",
        "failed" => "测试未通过：" + item.Report?.Message,
        "testing" => "正在检测，可稍后刷新页面",
        _ => item.Report?.Message ?? "尚未测试"
    };
}
