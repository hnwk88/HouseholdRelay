using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace HouseholdRelay.Pages;

public sealed class PricingModel(UsagePricing pricing, UpstreamService upstreams) : PageModel
{
    [BindProperty] public long Version { get; set; }
    [BindProperty(SupportsGet = true)] public string? ModelName { get; set; }
    [BindProperty] public string Currency { get; set; } = "USD";
    [BindProperty] public decimal? InputPrice { get; set; }
    [BindProperty] public decimal? OutputPrice { get; set; }
    [BindProperty] public decimal? CacheReadPrice { get; set; }
    [BindProperty] public decimal? CacheWritePrice { get; set; }
    public IReadOnlyList<UsagePrice> Prices { get; private set; } = [];
    public string? Notice { get; private set; }
    public string UpstreamAddress { get; private set; } = "";
    public void OnGet()
    {
        var active = upstreams.ReadActive(); Version = active.Version;
        if (string.IsNullOrEmpty(ModelName)) ModelName = active.Options.Model;
        Load();
        if (Prices.SingleOrDefault(item => item.Model == ModelName) is not { } price) return;
        Currency = price.Currency; InputPrice = price.InputPerMillion; OutputPrice = price.OutputPerMillion;
        CacheReadPrice = price.CacheReadPerMillion; CacheWritePrice = price.CacheWritePerMillion;
    }
    public void OnPost()
    {
        if (ModelState.IsValid)
        {
            try { pricing.Save(Version, ModelName ?? "", Currency ?? "", InputPrice, OutputPrice, CacheReadPrice, CacheWritePrice); Notice = "费率已保存，只影响之后发送的请求。历史金额保持不变。"; }
            catch (InvalidOperationException error) { ModelState.AddModelError("", error.Message); }
        }
        Load();
    }
    private void Load()
    { var active = upstreams.ReadActive(); UpstreamAddress = active.Options.BaseUrl; Prices = pricing.List(active.Version); }
}
