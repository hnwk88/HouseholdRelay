using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Application.Interfaces.Upstream;
using Application.Interfaces.Usage;
using Core.Usage;

namespace HouseholdRelay.Pages;

/// <summary>按活动上游版本与模型维护单价。</summary>
public sealed class PricingModel(IUsagePricingService pricing, IUpstreamService upstreams) : PageModel
{
    /// <summary>打开页面时活动上游的版本号；费率绑定到这个上游版本，保存时若活动上游已切换则由费率仓储拒绝，避免把单价记到别的上游名下。</summary>
    [BindProperty] public long Version { get; set; }
    /// <summary>要维护单价的模型名；可经查询参数预选，缺省时取活动上游配置的模型。</summary>
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
    /// <summary>当前活动上游版本下已保存的全部模型单价，供页面列表展示。</summary>
    public IReadOnlyList<UsagePrice> Prices { get; private set; } = [];
    /// <summary>保存成功后的提示，说明新费率只影响之后的请求。</summary>
    public string? Notice { get; private set; }
    /// <summary>活动上游的基础地址，让管理员确认正在给哪个上游定价。</summary>
    public string UpstreamAddress { get; private set; } = "";
    /// <summary>打开费率页（需已登录）：记录活动上游版本，选定模型后若已有单价则回填到表单，只读。</summary>
    public void OnGet()
    {
        var active = upstreams.ReadActive(); Version = active.Version;
        if (string.IsNullOrEmpty(ModelName)) ModelName = active.Options.Model;
        Load();
        if (Prices.SingleOrDefault(item => item.Model == ModelName) is not { } price) return;
        Currency = price.Currency; InputPrice = price.InputPerMillion; OutputPrice = price.OutputPerMillion;
        CacheReadPrice = price.CacheReadPerMillion; CacheWritePrice = price.CacheWritePerMillion;
    }
    /// <summary>保存某模型单价（需已登录且通过防 CSRF 校验）：按打开页面时的上游版本写入或覆盖；新费率只影响之后发送的请求，历史金额因发送时已复制快照而不变。</summary>
    public void OnPost()
    {
        if (ModelState.IsValid)
        {
            try { pricing.Save(Version, ModelName ?? "", Currency ?? "", InputPrice, OutputPrice, CacheReadPrice, CacheWritePrice); Notice = "费率已保存，只影响之后发送的请求。历史金额保持不变。"; }
            // 模型名、币种或单价不合法、活动上游已切换时都抛 InvalidOperationException，作为表单错误提示管理员刷新或改正，不写入任何单价。
            catch (InvalidOperationException error) { ModelState.AddModelError("", error.Message); }
        }
        Load();
    }
    /// <summary>按当前活动上游重新读取基础地址和该版本的单价列表；GET 与 POST 渲染前都会调用。</summary>
    private void Load()
    { var active = upstreams.ReadActive(); UpstreamAddress = active.Options.BaseUrl; Prices = pricing.List(active.Version); }
}
