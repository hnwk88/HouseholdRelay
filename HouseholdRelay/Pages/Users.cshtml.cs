using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace HouseholdRelay.Pages;

public sealed class UsersModel(IdentityService identities) : PageModel
{
    public IReadOnlyList<RelayUser> Users { get; private set; } = [];
    public IssuedToken? Issued { get; private set; }
    public string? Notice { get; private set; }
    public void OnGet() => Users = identities.List();
    public IActionResult OnPostCreate(string displayName) => Change(() => Issued = identities.Create(displayName ?? ""));
    public IActionResult OnPostRename(string userId, string displayName) => Change(() => identities.Rename(userId, displayName ?? ""));
    public IActionResult OnPostEnable(string userId, bool enabled) => Change(() => identities.SetEnabled(userId, enabled));
    public IActionResult OnPostRotate(string userId) => Change(() => Issued = identities.Rotate(userId));
    private IActionResult Change(Action action)
    {
        if (ModelState.IsValid)
        {
            try { action(); Notice = "已保存，新的权限检查立即生效。已发往上游的请求可以结束。"; }
            catch (InvalidOperationException error) { ModelState.AddModelError("", error.Message); }
        }
        Users = identities.List(); return Page();
    }
}
