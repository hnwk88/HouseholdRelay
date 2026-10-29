using Core.Configuration;

namespace HouseholdRelay.Pages;

/// <summary>设置页表单字段，与实例设置之间双向复制。</summary>
public sealed class SettingsInput
{
    /// <summary>家人访问用的对外 HTTPS 根地址；表单可留空，写回时去掉首尾空白，空值表示尚未配置。与证书域名的一致性在保存时校验。</summary>
    public string? PublicUrl { get; set; }
    /// <summary>对外 HTTPS 监听端口（1024–65535）；重启服务后生效。</summary>
    public int ListenPort { get; set; } = 8787;
    /// <summary>全家同时进行中的上游请求上限（1–8）；重启服务后生效。</summary>
    public int MaxConcurrent { get; set; } = 2;
    /// <summary>单个客户端同时进行中的请求上限（1–8），防止一人占满全局并发；重启服务后生效。</summary>
    public int MaxPerClient { get; set; } = 1;
    /// <summary>同时排队的请求上限（1–64），超出立即返回 429；重启服务后生效。</summary>
    public int MaxQueued { get; set; } = 16;
    /// <summary>每个客户端每个 UTC 整分钟允许的请求数（1–10000），超出返回 429；重启服务后生效。</summary>
    public int RequestsPerMinute { get; set; } = 120;
    /// <summary>排队等待并发名额的最长秒数（1–120），超时返回 429；重启服务后生效。</summary>
    public int QueueTimeoutSeconds { get; set; } = 30;
    /// <summary>读取客户端请求体的最长秒数（1–120），防止慢速上传长期占用连接；重启服务后生效。</summary>
    public int UploadTimeoutSeconds { get; set; } = 30;
    /// <summary>单个中转请求的总超时秒数（1–3600），默认 10 分钟以容纳长流式回复；重启服务后生效。</summary>
    public int RequestTimeoutSeconds { get; set; } = 600;
    /// <summary>为真时，OAuth 模式下把请求体中的设备元数据统一改写为本实例的设备标识；API 密钥模式不改写。</summary>
    public bool NormalizeDeviceMetadata { get; set; } = true;
    /// <summary>发往上游时使用的 User-Agent；不能为空白、不超过 256 字符且不含控制字符。</summary>
    public string UpstreamUserAgent { get; set; } = "HouseholdRelay/1.0";
    /// <summary>全局代理方式：<c>none</c>、<c>auto</c> 或 <c>manual</c>；保存后新建立的上游连接立即按新方式走，不用重启。</summary>
    public string ProxyMode { get; set; } = "none";
    /// <summary>手动指定的代理地址；可以省略 http://，写回时补齐；其余方式下原样保存。</summary>
    public string? ProxyUrl { get; set; }
    /// <summary>由当前实例设置生成表单初值，只复制设置页可编辑的字段。</summary>
    public static SettingsInput From(RelayConfiguration config) => new()
    {
        PublicUrl = config.PublicUrl, ListenPort = config.ListenPort, MaxConcurrent = config.MaxConcurrent,
        MaxPerClient = config.MaxPerClient, MaxQueued = config.MaxQueued, RequestsPerMinute = config.RequestsPerMinute,
        QueueTimeoutSeconds = config.QueueTimeoutSeconds, UploadTimeoutSeconds = config.UploadTimeoutSeconds,
        RequestTimeoutSeconds = config.RequestTimeoutSeconds, NormalizeDeviceMetadata = config.NormalizeDeviceMetadata,
        UpstreamUserAgent = config.UpstreamUserAgent, ProxyMode = config.ProxyMode, ProxyUrl = config.ProxyUrl
    };
    /// <summary>把表单字段写回传入的设置对象，只覆盖本类列出的字段，其余设置（证书、设备标识等）原样保留；不做校验，校验在配置服务保存时进行。</summary>
    public void Apply(RelayConfiguration config)
    {
        config.PublicUrl = PublicUrl?.Trim() ?? ""; config.ListenPort = ListenPort;
        config.MaxConcurrent = MaxConcurrent; config.MaxPerClient = MaxPerClient;
        config.MaxQueued = MaxQueued; config.RequestsPerMinute = RequestsPerMinute;
        config.QueueTimeoutSeconds = QueueTimeoutSeconds; config.UploadTimeoutSeconds = UploadTimeoutSeconds;
        config.RequestTimeoutSeconds = RequestTimeoutSeconds; config.NormalizeDeviceMetadata = NormalizeDeviceMetadata;
        config.UpstreamUserAgent = UpstreamUserAgent; config.ProxyMode = ProxyMode;
        var proxy = ProxyUrl?.Trim();
        config.ProxyUrl = string.IsNullOrEmpty(proxy) ? null : proxy.Contains("://", StringComparison.Ordinal) ? proxy : "http://" + proxy;
    }
}
