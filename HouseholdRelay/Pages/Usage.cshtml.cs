using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace HouseholdRelay.Pages;

public sealed class UsageModel(UsageStore usage, IdentityService identities) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? UserId { get; set; }
    [BindProperty(SupportsGet = true)] public DateTimeOffset? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateTimeOffset? Until { get; set; }
    [BindProperty(SupportsGet = true)] public string? Kind { get; set; }
    [BindProperty(SupportsGet = true)] public string? ModelName { get; set; }
    [BindProperty(SupportsGet = true)] public string? Outcome { get; set; }
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
    [BindProperty(SupportsGet = true)] public string? Granularity { get; set; }
    [BindProperty(SupportsGet = true)] public string? TimeZone { get; set; }
    public IReadOnlyList<RelayUser> Users { get; private set; } = [];
    public UsageReport? Report { get; private set; }
    public void OnGet()
    {
        Granularity ??= "day"; TimeZone ??= TimeZoneInfo.Local.Id;
        Users = identities.List();
        if (!ModelState.IsValid) return;
        try { Report = usage.Query(new(UserId, From, Until, Kind, ModelName, Outcome, PageNumber, Granularity, TimeZone)); }
        catch (InvalidOperationException error) { ModelState.AddModelError("", error.Message); }
    }
    public string PageLink(int page) => Url.Page("/Usage", new { UserId, From = From?.ToString("O"), Until = Until?.ToString("O"), Kind, ModelName, Outcome, Granularity, TimeZone, PageNumber = page })!;
}
