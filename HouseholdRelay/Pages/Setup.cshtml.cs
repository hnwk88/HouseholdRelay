using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace HouseholdRelay.Pages;

public sealed class SetupModel(AdministratorService administrators) : PageModel
{
    [BindProperty] public string Code { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";
    [BindProperty] public string Confirmation { get; set; } = "";
    public IActionResult OnGet() => administrators.IsInitialized ? NotFound() : Page();
    public IActionResult OnPost()
    {
        if (administrators.IsInitialized) return NotFound();
        if (Password != Confirmation) ModelState.AddModelError("", "两次输入的管理密码不一致。");
        if (ModelState.IsValid)
        {
            try { administrators.Initialize(Code, Password); return RedirectToPage("/Login", new { setup = true }); }
            catch (InvalidOperationException error) { ModelState.AddModelError("", error.Message); }
        }
        Password = Confirmation = Code = "";
        ModelState.Remove(nameof(Password)); ModelState.Remove(nameof(Confirmation)); ModelState.Remove(nameof(Code));
        return Page();
    }
}
