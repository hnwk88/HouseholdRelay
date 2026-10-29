using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Application.Interfaces.Administration;
using HouseholdRelay.Management;

namespace HouseholdRelay.Pages;

/// <summary>管理员退出：只接受 POST（带防 CSRF 令牌），避免第三方链接诱导 GET 把管理员登出。</summary>
public sealed class LogoutModel(IAdministratorService administrators) : PageModel
{
    /// <summary>先在服务端作废当前会话标识，再清除管理 Cookie；即使旧 Cookie 被留存也无法再通过会话校验。</summary>
    public async Task<IActionResult> OnPostAsync()
    {
        administrators.Logout(User.FindFirstValue(ManagementWeb.SessionClaim));
        await HttpContext.SignOutAsync(ManagementWeb.Scheme);
        return RedirectToPage("/Login");
    }
}
