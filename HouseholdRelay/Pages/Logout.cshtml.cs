using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace HouseholdRelay.Pages;

public sealed class LogoutModel(AdministratorService administrators) : PageModel
{
    public async Task<IActionResult> OnPostAsync()
    {
        administrators.Logout(User.FindFirstValue(ManagementWeb.SessionClaim));
        await HttpContext.SignOutAsync(ManagementWeb.Scheme);
        return RedirectToPage("/Login");
    }
}
