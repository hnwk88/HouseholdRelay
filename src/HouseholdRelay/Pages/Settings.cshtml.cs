using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Application.Interfaces.Configuration;

namespace HouseholdRelay.Pages;

/// <summary>中转限制、对外地址与上游请求特征设置。</summary>
public sealed class SettingsModel(IConfigurationService settings) : PageModel
{
    /// <summary>打开页面时的设置版本号，用于乐观并发：保存时版本不一致即拒绝，防止覆盖别处刚保存的设置；保存成功后在页面上加一。</summary>
    [BindProperty] public long Version { get; set; }
    /// <summary>表单回传的全部可编辑设置字段。</summary>
    [BindProperty] public SettingsInput Input { get; set; } = new();
    /// <summary>保存成功后的提示，说明监听端口和转发限制须重启服务才生效。</summary>
    public string? Notice { get; private set; }
    /// <summary>打开设置页（需已登录）：读取当前设置快照与版本号填入表单，只读。</summary>
    public void OnGet()
    {
        var snapshot = settings.Read(); Version = snapshot.Version;
        Input = SettingsInput.From(snapshot.Value);
    }
    /// <summary>保存设置（需已登录且通过防 CSRF 校验）：在最新设置上覆盖表单字段后按版本号保存，不触碰表单之外的配置项；监听端口和转发限制须重启服务才生效，保存地址也不会自动建立公网映射。</summary>
    public IActionResult OnPost()
    {
        if (ModelState.IsValid)
        {
            try
            {
                var config = settings.Read().Value; Input.Apply(config); settings.Save(Version, config);
                Notice = "设置已保存。监听端口和转发限制在重启服务后生效；保存地址不会自动建立公网连接。";
                Version++; ModelState.Remove(nameof(Version));
            }
            // 版本冲突或校验不通过（如对外地址与已导入证书的域名不符）都抛 InvalidOperationException；只作表单错误显示，设置保持原样，由管理员重新加载后决定。
            catch (InvalidOperationException error) { ModelState.AddModelError("", error.Message); }
        }
        return Page();
    }
}
