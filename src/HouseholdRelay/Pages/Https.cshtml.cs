using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Application.Interfaces.Certificates;
using Application.Interfaces.Configuration;
using Core.Certificates;
using HouseholdRelay.Relay;
using Infrastructure.ExternalSources.Upstream;

namespace HouseholdRelay.Pages;

/// <summary>创建私有证书或导入 PFX，统一验证地址并保护秘密输入。</summary>
public sealed class HttpsModel(IConfigurationService settings, ICertificateService certificates, RelayServer relay) : PageModel
{
    /// <summary>表单回传的设置版本号，用于乐观并发：保存时与当前版本不一致即拒绝，防止覆盖他人在别的标签页刚做的修改；保存成功后刷新为新版本。</summary>
    [BindProperty] public long Version { get; set; }
    /// <summary>家人访问中转用的对外 HTTPS 地址；保存时须与证书域名一致，否则由证书服务拒绝。</summary>
    [BindProperty] public string PublicUrl { get; set; } = "";
    /// <summary>本机对外 HTTPS 监听端口；修改后须重启服务才生效。</summary>
    [BindProperty] public int ListenPort { get; set; }
    /// <summary>上传的可信证书 PFX 文件，限 1 MB 以内；处理完无论成败都清空，不回显到页面。</summary>
    [BindProperty] public IFormFile? CertificateFile { get; set; }
    /// <summary>PFX 文件的保护密码；只在本次导入时使用，处理完无论成败都清空，不回填到表单。</summary>
    [BindProperty] public string? Password { get; set; }
    /// <summary>创建私有 CA 时是否允许替换已存在的证书；不勾选时已有证书会被拒绝覆盖。</summary>
    [BindProperty] public bool ReplaceExisting { get; set; }
    /// <summary>当前已保存证书的摘要信息（模式、有效期等）；未配置证书时为空。</summary>
    public CertificateInfo? Info { get; private set; }
    /// <summary>私有 CA 根证书的 SHA-256 指纹，供管理员与客户端核对；未生成私有 CA 时为空。</summary>
    public string? AuthorityFingerprint { get; private set; }
    /// <summary>对外 HTTPS 中转本次进程是否已真正启动监听；新导入的证书在重启前不会让它变为真。</summary>
    public bool Listening => relay.HttpsActive;
    /// <summary>操作成功后的提示文字，提醒需要重启服务才能生效。</summary>
    public string? Notice { get; private set; }
    /// <summary>打开 HTTPS 设置页（需已登录）：读取当前设置版本、对外地址、监听端口与证书信息，只读。</summary>
    public void OnGet()
    {
        var value = settings.Read(); Version = value.Version; PublicUrl = value.Value.PublicUrl; ListenPort = value.Value.ListenPort; Load();
    }
    /// <summary>导入可信 PFX 证书并保存地址与端口（需已登录且通过防 CSRF 校验）：先限长读取文件，导入后立即清零证书字节，并清空密码与文件绑定值；需重启服务才生效。</summary>
    public async Task<IActionResult> OnPostAsync()
    {
        try
        {
            if (ModelState.IsValid)
            {
                if (CertificateFile is null || CertificateFile.Length is 0 or > 1024 * 1024) throw new InvalidOperationException("请选择小于 1 MB 的 PFX 证书文件。");
                await using var input = CertificateFile.OpenReadStream();
                var bytes = await UpstreamResponses.ReadBounded(input, 1024 * 1024, HttpContext.RequestAborted);
                try { certificates.Import(Version, PublicUrl, ListenPort, bytes, Password); }
                // PFX 字节里含服务器私钥，导入成功或失败都立即清零，不让私钥留在托管内存里等待回收。
                finally { CryptographicOperations.ZeroMemory(bytes); }
                Notice = "可信证书及地址已保存。请重启服务使监听端口和证书生效，再从实际客户端验证连接。";
                Version = settings.Read().Version; ModelState.Remove(nameof(Version));
            }
        }
        // 校验失败、版本冲突、域名不符等业务拒绝都抛 InvalidOperationException，消息是给管理员的中文提示，显示在表单上而不是变成 503。
        catch (InvalidOperationException error) { ModelState.AddModelError("", error.Message); }
        // 无论成败都清掉密码和上传文件的绑定值与模型状态，确保重新渲染的页面不会把 PFX 密码回填到表单里。
        finally { Password = null; CertificateFile = null; ModelState.Remove(nameof(Password)); ModelState.Remove(nameof(CertificateFile)); }
        Load(); return Page();
    }
    /// <summary>创建私有 CA 及服务器证书并保存地址与端口（需已登录且通过防 CSRF 校验）；已有证书时须勾选 <see cref="ReplaceExisting"/> 才会替换。需重启服务生效，客户端须核对指纹后安装一次信任。</summary>
    public IActionResult OnPostPrivate()
    {
        try
        {
            if (ModelState.IsValid)
            {
                certificates.CreatePrivate(Version, PublicUrl, ListenPort, ReplaceExisting);
                Notice = "私有 CA 及服务器证书已创建。请重启服务，并在每台客户端核对指纹、安装一次信任。";
                Version = settings.Read().Version; ModelState.Remove(nameof(Version));
            }
        }
        // 版本冲突、地址不合法或拒绝覆盖已有证书等业务拒绝只显示为表单错误，不改动已保存的证书。
        catch (InvalidOperationException error) { ModelState.AddModelError("", error.Message); }
        Load(); return Page();
    }
    /// <summary>重新读取证书摘要与 CA 指纹，供每个处理程序在渲染页面前刷新展示内容。</summary>
    private void Load() { Info = certificates.ReadInfo(); AuthorityFingerprint = certificates.AuthorityFingerprint(); }
}
