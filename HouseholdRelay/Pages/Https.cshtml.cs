using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace HouseholdRelay.Pages;

/// <summary>创建私有证书或导入 PFX，统一验证地址并保护秘密输入。</summary>
public sealed class HttpsModel(ConfigurationService settings, CertificateService certificates, RelayServer relay) : PageModel
{
    [BindProperty] public long Version { get; set; }
    [BindProperty] public string PublicUrl { get; set; } = "";
    [BindProperty] public int ListenPort { get; set; }
    [BindProperty] public IFormFile? CertificateFile { get; set; }
    [BindProperty] public string? Password { get; set; }
    [BindProperty] public bool ReplaceExisting { get; set; }
    public CertificateInfo? Info { get; private set; }
    public string? AuthorityFingerprint { get; private set; }
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
                Notice = "可信证书及地址已保存。请重启服务使监听端口和证书生效，再从实际客户端验证连接。";
                Version = settings.Read().Version; ModelState.Remove(nameof(Version));
            }
        }
        catch (InvalidOperationException error) { ModelState.AddModelError("", error.Message); }
        finally { Password = null; CertificateFile = null; ModelState.Remove(nameof(Password)); ModelState.Remove(nameof(CertificateFile)); }
        Load(); return Page();
    }
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
        catch (InvalidOperationException error) { ModelState.AddModelError("", error.Message); }
        Load(); return Page();
    }
    private void Load() { Info = certificates.ReadInfo(); AuthorityFingerprint = certificates.AuthorityFingerprint(); }
}
