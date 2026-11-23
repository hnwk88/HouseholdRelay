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
    /// <summary>回发后焦点要落的元素编号：新令牌的标题，或刚处理的那一人的那个按钮；为空时由站点脚本按结果提示或错误决定。</summary>
    public string? FocusId { get; private set; }
    /// <summary>「添加使用者」显示名输入框的字段键。</summary>
    public const string CreateKey = "displayName";
    /// <summary>某一人「显示名」输入框的字段键。</summary>
    public static string RenameKey(long userId) => "displayName-" + userId;
    /// <summary>打开使用者管理页（需已登录）：列出全部使用者，只读。</summary>
    public async Task OnGetAsync() => Users = await identities.ListAsync();
    /// <summary>新增使用者并签发个人令牌（需已登录且通过防 CSRF 校验）；令牌只在本次页面展示一次。</summary>
    public Task<IActionResult> OnPostCreateAsync(string displayName) => ChangeAsync(async () => Issued = await identities.CreateAsync(displayName ?? ""), CreateKey, "token-heading");
    /// <summary>修改使用者显示名（需已登录且通过防 CSRF 校验）；不影响其令牌。</summary>
    public Task<IActionResult> OnPostRenameAsync(long userId, string displayName) => ChangeAsync(() => identities.RenameAsync(userId, displayName ?? ""), RenameKey(userId), "rename-" + userId);
    /// <summary>启用或停用使用者（需已登录且通过防 CSRF 校验）；停用后新请求立即被拒，已发往上游的请求可以结束。</summary>
    public Task<IActionResult> OnPostEnableAsync(long userId, bool enabled) => ChangeAsync(() => identities.SetEnabledAsync(userId, enabled), "", "enable-" + userId);
    /// <summary>轮换个人令牌（需已登录且通过防 CSRF 校验）：旧令牌立即失效，新令牌只在本次页面展示一次。</summary>
    public Task<IActionResult> OnPostRotateAsync(long userId) => ChangeAsync(async () => Issued = await identities.RotateAsync(userId), "", "token-heading");
    /// <summary>
    /// 各写操作的公共流程：模型绑定通过才执行，业务拒绝转成表单错误（<paramref name="errorKey"/> 非空时挂到那个显示名输入框上，否则留在页面级摘要），
    /// 成功时焦点去 <paramref name="focus"/>；最后重新读取使用者列表并渲染页面。
    /// </summary>
    private async Task<IActionResult> ChangeAsync(Func<Task> action, string errorKey, string focus)
    {
        if (ModelState.IsValid)
        {
            try { await action(); Notice = "已保存，新的权限检查立即生效。已发往上游的请求可以结束。"; FocusId = focus; }
            // 显示名不合法、人数已满、使用者不存在等业务拒绝只显示为表单错误，身份库保持原样。
            catch (InvalidOperationException error) { ModelState.AddModelError(errorKey, error.Message); }
        }
        Users = await identities.ListAsync(); return Page();
    }
}
