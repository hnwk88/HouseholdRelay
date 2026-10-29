using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Application.Interfaces.Administration;
using HouseholdRelay.Management;

namespace HouseholdRelay.Pages;

/// <summary>管理员登录页，允许匿名访问（其余页面都要求登录）；尚未完成首次设置时一律转到 Setup。登录 POST 受每分钟 5 次的限流保护，并照常校验防 CSRF 令牌。</summary>
public sealed class LoginModel(IAdministratorService administrators) : PageModel
{
    /// <summary>表单提交的管理密码；校验失败后立即清空并移出模型状态，页面不会回填。</summary>
    [BindProperty] public string Password { get; set; } = "";
    /// <summary>查询参数标记：为真表示刚完成首次设置跳转而来，页面只据此显示一条提示，不影响鉴权。</summary>
    [BindProperty(SupportsGet = true)] public bool Setup { get; set; }
    /// <summary>打开登录页：未初始化则转到 Setup，已登录则直接进首页，否则显示登录表单。</summary>
    public IActionResult OnGet()
    {
        if (!administrators.IsInitialized) return RedirectToPage("/Setup");
        return User.Identity?.IsAuthenticated == true ? RedirectToPage("/Index") : Page();
    }
    /// <summary>提交登录：密码正确时由管理员服务签发会话标识，写入非持久的管理 Cookie（关闭浏览器即失效，之后每次请求还会核对会话是否仍有效）；密码错误只给统一提示，不区分原因。</summary>
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
