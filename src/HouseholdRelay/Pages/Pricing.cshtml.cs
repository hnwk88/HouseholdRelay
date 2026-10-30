using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Application.Interfaces.Upstream;
using Application.Interfaces.Usage;
using Core.Upstream;
using Core.Usage;

namespace HouseholdRelay.Pages;

/// <summary>按已保存上游与模型维护单价：自动切换换到哪一家，就按那一家的单价估算。</summary>
public sealed class PricingModel(IUsagePricingService pricing, IUpstreamService upstreams) : PageModel
{
    /// <summary>正在维护单价的已保存上游主键；可经查询参数选择，缺省时取当前在用的那一家。保存时那一家已被删除则由费率仓储拒绝。</summary>
    [BindProperty(SupportsGet = true)] public string? CandidateId { get; set; }
    /// <summary>要维护单价的模型名；可经查询参数预选，缺省时取所选上游配置的模型。</summary>
    [BindProperty(SupportsGet = true)] public string? ModelName { get; set; }
    /// <summary>单价的币种代码，默认 USD。</summary>
    [BindProperty] public string Currency { get; set; } = "USD";
    /// <summary>每百万输入 token 的单价；单价未知时留空，四项单价至少要填一项。</summary>
    [BindProperty] public decimal? InputPrice { get; set; }
    /// <summary>每百万输出 token 的单价；单价未知时留空，四项单价至少要填一项。</summary>
    [BindProperty] public decimal? OutputPrice { get; set; }
    /// <summary>每百万缓存读取 token 的单价；单价未知时留空，四项单价至少要填一项。</summary>
    [BindProperty] public decimal? CacheReadPrice { get; set; }
    /// <summary>每百万缓存写入 token 的单价；单价未知时留空，四项单价至少要填一项。</summary>
    [BindProperty] public decimal? CacheWritePrice { get; set; }
    /// <summary>所选上游下已保存的全部模型单价，供页面列表展示。</summary>
    public IReadOnlyList<UsagePrice> Prices { get; private set; } = [];
    /// <summary>全部已保存的上游，按上游页顺序，供选择给哪一家定价。</summary>
    public IReadOnlyList<UpstreamCandidate> Saved { get; private set; } = [];
    /// <summary>所选上游；一个已保存上游都没有、或所选的已被删除时为 null。</summary>
    public UpstreamCandidate? Selected { get; private set; }
    /// <summary>当前在用那一家的主键，选项里据此标出「在用」。</summary>
    public string? ActiveId { get; private set; }
    /// <summary>保存成功后的提示，说明新费率只影响之后的请求。</summary>
    public string? Notice { get; private set; }

    /// <summary>打开费率页（需已登录）：选定上游（缺省为在用的那一家），选定模型后若已有单价则回填到表单，只读。</summary>
    public void OnGet()
    {
        Load();
        if (string.IsNullOrEmpty(ModelName)) ModelName = Selected?.Options.Model;
        if (Prices.SingleOrDefault(item => item.Model == ModelName) is not { } price) return;
        Currency = price.Currency; InputPrice = price.InputPerMillion; OutputPrice = price.OutputPerMillion;
        CacheReadPrice = price.CacheReadPerMillion; CacheWritePrice = price.CacheWritePerMillion;
    }

    /// <summary>保存某上游某模型的单价（需已登录且通过防 CSRF 校验）：写入或覆盖；新费率只影响之后发送的请求，历史金额因发送时已复制快照而不变。</summary>
    public void OnPost()
    {
        if (ModelState.IsValid)
        {
            try { pricing.Save(CandidateId ?? "", ModelName ?? "", Currency ?? "", InputPrice, OutputPrice, CacheReadPrice, CacheWritePrice); Notice = "费率已保存，只影响之后发送的请求。历史金额保持不变。"; }
            // 模型名、币种或单价不合法、所选上游已被删除时都抛 InvalidOperationException，作为表单错误提示管理员刷新或改正，不写入任何单价。
            catch (InvalidOperationException error) { ModelState.AddModelError("", error.Message); }
        }
        Load();
    }

    /// <summary>读取已保存上游列表与在用那一家，确定所选上游，再读它的单价列表；GET 与 POST 渲染前都会调用。</summary>
    private void Load()
    {
        Saved = upstreams.ListCandidates(); ActiveId = upstreams.ReadOverview().CandidateId;
        CandidateId ??= ActiveId;
        Selected = Saved.FirstOrDefault(item => item.Id == CandidateId);
        Prices = Selected is null ? [] : pricing.List(Selected.Id);
    }
}
