using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Application.Interfaces.Identity;
using Core.Identity;

namespace HouseholdRelay.Pages;

/// <summary>使用者与个人令牌管理。</summary>
public sealed class UsersModel(IIdentityService identities) : PageModel
{
    /// <summary>全部使用者（含已停用的），供页面列表展示；不含任何令牌。</summary>
    public IReadOnlyList<RelayUser> Users { get; private set; } = [];
    /// <summary>刚新增或轮换得到的个人令牌，只在本次响应中展示一次；明文不进入持久化模型，之后无法再取回。</summary>
    public IssuedToken? Issued { get; private set; }
    /// <summary>操作成功后的提示，说明权限变化立即生效。</summary>
    public string? Notice { get; private set; }
    /// <summary>打开使用者管理页（需已登录）：列出全部使用者，只读。</summary>
    public void OnGet() => Users = identities.List();
    /// <summary>新增使用者并签发个人令牌（需已登录且通过防 CSRF 校验）；令牌只在本次页面展示一次。</summary>
    public IActionResult OnPostCreate(string displayName) => Change(() => Issued = identities.Create(displayName ?? ""));
    /// <summary>修改使用者显示名（需已登录且通过防 CSRF 校验）；不影响其令牌。</summary>
    public IActionResult OnPostRename(string userId, string displayName) => Change(() => identities.Rename(userId, displayName ?? ""));
    /// <summary>启用或停用使用者（需已登录且通过防 CSRF 校验）；停用后新请求立即被拒，已发往上游的请求可以结束。</summary>
    public IActionResult OnPostEnable(string userId, bool enabled) => Change(() => identities.SetEnabled(userId, enabled));
    /// <summary>轮换个人令牌（需已登录且通过防 CSRF 校验）：旧令牌立即失效，新令牌只在本次页面展示一次。</summary>
    public IActionResult OnPostRotate(string userId) => Change(() => Issued = identities.Rotate(userId));
    /// <summary>各写操作的公共流程：模型绑定通过才执行，业务拒绝转成表单错误，最后重新读取使用者列表并渲染页面。</summary>
    private IActionResult Change(Action action)
    {
        if (ModelState.IsValid)
        {
            try { action(); Notice = "已保存，新的权限检查立即生效。已发往上游的请求可以结束。"; }
            // 显示名不合法、使用者不存在等业务拒绝只显示为表单错误，身份库保持原样。
            catch (InvalidOperationException error) { ModelState.AddModelError("", error.Message); }
        }
        Users = identities.List(); return Page();
    }
}
