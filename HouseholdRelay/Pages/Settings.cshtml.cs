using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace HouseholdRelay.Pages;

public sealed class SettingsModel(ConfigurationService settings) : PageModel
{
    [BindProperty] public long Version { get; set; }
    [BindProperty] public SettingsInput Input { get; set; } = new();
    public string? Notice { get; private set; }
    public void OnGet()
    {
        var snapshot = settings.Read(); Version = snapshot.Version;
        Input = SettingsInput.From(snapshot.Value);
    }
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
            catch (InvalidOperationException error) { ModelState.AddModelError("", error.Message); }
        }
        return Page();
    }
}

public sealed class SettingsInput
{
    public string? PublicUrl { get; set; }
    public int ListenPort { get; set; } = 8787;
    public int MaxConcurrent { get; set; } = 2;
    public int MaxPerClient { get; set; } = 1;
    public int MaxQueued { get; set; } = 16;
    public int RequestsPerMinute { get; set; } = 120;
    public int QueueTimeoutSeconds { get; set; } = 30;
    public int UploadTimeoutSeconds { get; set; } = 30;
    public int RequestTimeoutSeconds { get; set; } = 600;
    public bool NormalizeDeviceMetadata { get; set; } = true;
    public string UpstreamUserAgent { get; set; } = "HouseholdRelay/1.0";
    public static SettingsInput From(RelayConfiguration config) => new()
    {
        PublicUrl = config.PublicUrl, ListenPort = config.ListenPort, MaxConcurrent = config.MaxConcurrent,
        MaxPerClient = config.MaxPerClient, MaxQueued = config.MaxQueued, RequestsPerMinute = config.RequestsPerMinute,
        QueueTimeoutSeconds = config.QueueTimeoutSeconds, UploadTimeoutSeconds = config.UploadTimeoutSeconds,
        RequestTimeoutSeconds = config.RequestTimeoutSeconds, NormalizeDeviceMetadata = config.NormalizeDeviceMetadata,
        UpstreamUserAgent = config.UpstreamUserAgent
    };
    public void Apply(RelayConfiguration config)
    {
        config.PublicUrl = PublicUrl?.Trim() ?? ""; config.ListenPort = ListenPort;
        config.MaxConcurrent = MaxConcurrent; config.MaxPerClient = MaxPerClient;
        config.MaxQueued = MaxQueued; config.RequestsPerMinute = RequestsPerMinute;
        config.QueueTimeoutSeconds = QueueTimeoutSeconds; config.UploadTimeoutSeconds = UploadTimeoutSeconds;
        config.RequestTimeoutSeconds = RequestTimeoutSeconds; config.NormalizeDeviceMetadata = NormalizeDeviceMetadata;
        config.UpstreamUserAgent = UpstreamUserAgent;
    }
}
