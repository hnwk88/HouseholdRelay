using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace HouseholdRelay.Pages;

public sealed class LoginModel(AdministratorService administrators) : PageModel
{
    [BindProperty] public string Password { get; set; } = "";
    [BindProperty(SupportsGet = true)] public bool Setup { get; set; }
    public IActionResult OnGet()
    {
        if (!administrators.IsInitialized) return RedirectToPage("/Setup");
        return User.Identity?.IsAuthenticated == true ? RedirectToPage("/Index") : Page();
    }
    public async Task<IActionResult> OnPostAsync()
    {
        if (!administrators.IsInitialized) return RedirectToPage("/Setup");
        var session = ModelState.IsValid ? administrators.Login(Password) : null;
        if (session is not null)
        {
            var principal = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ManagementWeb.SessionClaim, session) }, ManagementWeb.Scheme));
            await HttpContext.SignInAsync(ManagementWeb.Scheme, principal, new AuthenticationProperties { IsPersistent = false });
            return RedirectToPage("/Index");
        }
        Password = ""; ModelState.Remove(nameof(Password)); ModelState.AddModelError("", "管理密码不正确。");
        return Page();
    }
}
