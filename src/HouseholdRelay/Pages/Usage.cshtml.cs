using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Application.Interfaces.Identity;
using Application.Interfaces.Usage;
using Core.Identity;
using Core.Usage;

namespace HouseholdRelay.Pages;

/// <summary>管理员查看全部使用者的用量明细与统计。</summary>
public sealed class UsageModel(IUsageService usage, IIdentityService identities) : PageModel
{
    /// <summary>查询参数：只看某个使用者的用量，空表示全部使用者；管理员入口才允许按任意使用者筛选。</summary>
    [BindProperty(SupportsGet = true)] public string? UserId { get; set; }
    /// <summary>查询参数：起始时间（含），空表示不限；不能晚于 <see cref="Until"/>。</summary>
    [BindProperty(SupportsGet = true)] public DateTimeOffset? From { get; set; }
    /// <summary>查询参数：截止时间，空表示不限。</summary>
    [BindProperty(SupportsGet = true)] public DateTimeOffset? Until { get; set; }
    /// <summary>查询参数：接口类别，只能是 inference、estimate、models 之一，空表示不筛选。</summary>
    [BindProperty(SupportsGet = true)] public string? Kind { get; set; }
    /// <summary>查询参数：模型名（不超过 160 字符），空表示不筛选。</summary>
    [BindProperty(SupportsGet = true)] public string? ModelName { get; set; }
    /// <summary>查询参数：请求结果，只能是 started、succeeded、failed、cancelled、interrupted 之一，空表示不筛选。</summary>
    [BindProperty(SupportsGet = true)] public string? Outcome { get; set; }
    /// <summary>查询参数：明细页码，从 1 开始；每页行数固定，不由参数调整。</summary>
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
    /// <summary>查询参数：统计粒度，day 或 hour，缺省为 day。</summary>
    [BindProperty(SupportsGet = true)] public string? Granularity { get; set; }
    /// <summary>查询参数：统计分段所用的时区标识，缺省为本机时区；必须是本机可识别的时区。</summary>
    [BindProperty(SupportsGet = true)] public string? TimeZone { get; set; }
    /// <summary>全部使用者列表，供筛选下拉框选择。</summary>
    public IReadOnlyList<RelayUser> Users { get; private set; } = [];
    /// <summary>查询得到的用量明细与统计；筛选条件不合法或模型绑定失败时为空。</summary>
    public UsageReport? Report { get; private set; }
    /// <summary>打开管理员用量页（需已登录）：补齐粒度与时区缺省值，校验筛选条件后查询报告，只读。</summary>
    public void OnGet()
    {
        Granularity ??= "day"; TimeZone ??= TimeZoneInfo.Local.Id;
        Users = identities.List();
        if (!ModelState.IsValid) return;
        try { Report = usage.Query(new(UserId, From, Until, Kind, ModelName, Outcome, PageNumber, Granularity, TimeZone)); }
        // 筛选条件不合法（时间倒置、未知类别或时区等）时抛 InvalidOperationException，只显示为页面错误，不展示报告。
        catch (InvalidOperationException error) { ModelState.AddModelError("", error.Message); }
    }
    /// <summary>生成翻到第 <paramref name="page"/> 页的链接，保留当前全部筛选条件；时间用 ISO 8601 往返格式，确保回传后解析不丢失时区偏移。</summary>
    public string PageLink(int page) => Url.Page("/Usage", new { UserId, From = From?.ToString("O"), Until = Until?.ToString("O"), Kind, ModelName, Outcome, Granularity, TimeZone, PageNumber = page })!;
}
