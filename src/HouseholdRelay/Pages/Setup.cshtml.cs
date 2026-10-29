using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Application.Interfaces.Administration;
using HouseholdRelay.Management;

namespace HouseholdRelay.Pages;

/// <summary>首次设置管理员密码，允许匿名访问但只在尚未初始化时可用，初始化后一律 404，不能借此重置管理员。提交须同时给出 serve 在本机控制台打印的引导码，POST 受每分钟 5 次的限流保护并照常校验防 CSRF 令牌。</summary>
public sealed class SetupModel(IAdministratorService administrators) : PageModel
{
    /// <summary>serve 启动时在本机控制台打印的一次性引导码，证明操作者能看到这台电脑的控制台；处理后清空，不回填。</summary>
    [BindProperty] public string Code { get; set; } = "";
    /// <summary>要设置的管理密码；处理后清空，不回填到表单。</summary>
    [BindProperty] public string Password { get; set; } = "";
    /// <summary>再次输入的管理密码，须与 <see cref="Password"/> 一致；处理后清空。</summary>
    [BindProperty] public string Confirmation { get; set; } = "";
    /// <summary>打开首次设置页：已初始化时返回 404，否则显示表单。</summary>
    public IActionResult OnGet() => administrators.IsInitialized ? NotFound() : Page();
    /// <summary>提交首次设置：已初始化则 404；两次密码一致且引导码正确时创建管理员并跳转登录页。失败时清空全部输入及其模型状态再显示错误。</summary>
    public IActionResult OnPost()
    {
        if (administrators.IsInitialized) return NotFound();
        if (Password != Confirmation) ModelState.AddModelError("", "两次输入的管理密码不一致。");
        if (ModelState.IsValid)
        {
            try { administrators.Initialize(Code, Password); return RedirectToPage("/Login", new { setup = true }); }
            // 密码长度不在 12–128 个字符、引导码不正确等业务拒绝都抛 InvalidOperationException，只显示为表单错误，不创建管理员。
            catch (InvalidOperationException error) { ModelState.AddModelError("", error.Message); }
        }
        Password = Confirmation = Code = "";
        ModelState.Remove(nameof(Password)); ModelState.Remove(nameof(Confirmation)); ModelState.Remove(nameof(Code));
        return Page();
    }
}
