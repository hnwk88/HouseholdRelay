using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace HouseholdRelay.Pages;

/// <summary>导入 PFX 并验证公开域名；凭据输入在返回页面前清除。</summary>
public sealed class HttpsModel(ConfigurationService settings, CertificateService certificates, RelayServer relay) : PageModel
{
    [BindProperty] public long Version { get; set; }
    [BindProperty] public string PublicUrl { get; set; } = "";
    [BindProperty] public int ListenPort { get; set; }
    [BindProperty] public IFormFile? CertificateFile { get; set; }
    [BindProperty] public string? Password { get; set; }
    public CertificateInfo? Info { get; private set; }
    public bool Legacy { get; private set; }
    public bool Listening => relay.HttpsActive;
    public string? Notice { get; private set; }
    public void OnGet()
    {
        var value = settings.Read(); Version = value.Version; PublicUrl = value.Value.PublicUrl; ListenPort = value.Value.ListenPort; Load();
    }
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
                finally { CryptographicOperations.ZeroMemory(bytes); }
                Notice = "可信证书及地址已保存。请重启服务使监听端口和证书生效，再从外部客户端验证公网连接。";
                Version = settings.Read().Version; ModelState.Remove(nameof(Version));
            }
        }
        catch (InvalidOperationException error) { ModelState.AddModelError("", error.Message); }
        finally { Password = null; CertificateFile = null; ModelState.Remove(nameof(Password)); ModelState.Remove(nameof(CertificateFile)); }
        Load(); return Page();
    }
    private void Load() { Info = certificates.ReadInfo(); Legacy = Info is null && settings.HasCertificate; }
}
