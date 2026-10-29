using System.Security.Cryptography;
using Core.Identity;

namespace Core.Configuration;

/// <summary>实例级中转设置；客户端名单只在旧数据迁移和内存夹具中携带，数据库设置里始终为空。</summary>
public sealed class RelayConfiguration
{
    /// <summary>HTTPS 监听端口，默认 8787；<see cref="Validate"/> 只接受 1024–65535，避开需要管理员权限的低端口。</summary>
    public int ListenPort { get; set; } = 8787;

    /// <summary>家人访问用的对外 HTTPS 根地址；空字符串表示尚未配置。格式与证书域名一致性由配置服务保存时另行校验。</summary>
    public string PublicUrl { get; set; } = "";

    /// <summary>上游认证方式：<c>oauth</c>（Bearer 令牌）或 <c>api-key</c>（x-api-key 头），其余取值不合法。</summary>
    public string AuthMode { get; set; } = "oauth";

    /// <summary>本实例固定的设备标识，64 位十六进制（32 字节随机数）；OAuth 模式下用于统一改写请求里的设备元数据，让上游只看到一台设备。</summary>
    public string DeviceId { get; set; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    /// <summary>发往上游时使用的 User-Agent；不能为空白、不超过 256 字符且不含控制字符。</summary>
    public string UpstreamUserAgent { get; set; } = "HouseholdRelay/1.0";

    /// <summary>为 true 时，OAuth 模式下把请求体中的设备元数据统一改写为 <see cref="DeviceId"/>；API 密钥模式不改写。</summary>
    public bool NormalizeDeviceMetadata { get; set; } = true;

    /// <summary>旧版 server.json 里的上游代理地址，只在旧版导入时读入；v4 迁移把它改成 <see cref="ProxyMode"/> 与 <see cref="ProxyUrl"/>，运行时不再使用。</summary>
    public string? UpstreamProxy { get; set; }

    /// <summary>全局出站代理方式：<c>none</c>（不使用代理）、<c>auto</c>（自动检测 Windows 系统代理）或 <c>manual</c>（使用 <see cref="ProxyUrl"/>）；上游转发、获取模型、测试和 Claude Code 检测子进程共用这一份。</summary>
    public string ProxyMode { get; set; } = "none";

    /// <summary>手动指定的代理根地址（如 <c>http://127.0.0.1:7897</c>）；只在 <see cref="ProxyMode"/> 为 <c>manual</c> 时生效，其余方式下原样保存，切回手动不必重填。</summary>
    public string? ProxyUrl { get; set; }

    /// <summary>全家同时进行中的上游请求上限（1–8），由准入器的全局信号量执行。</summary>
    public int MaxConcurrent { get; set; } = 2;

    /// <summary>单个客户端同时进行中的请求上限（1–8），防止一人占满全局并发。</summary>
    public int MaxPerClient { get; set; } = 1;

    /// <summary>同时处于排队（含正在等待名额）的请求上限（1–64）；超出时立即返回 429。</summary>
    public int MaxQueued { get; set; } = 16;

    /// <summary>请求排队等待并发名额的最长秒数（1–120），超时返回 429。</summary>
    public int QueueTimeoutSeconds { get; set; } = 30;

    /// <summary>读取客户端请求体的最长秒数（1–120），防止慢速上传长期占用连接。</summary>
    public int UploadTimeoutSeconds { get; set; } = 30;

    /// <summary>单个中转请求的总超时秒数（1–3600），默认 10 分钟以容纳长时间的流式回复。</summary>
    public int RequestTimeoutSeconds { get; set; } = 600;

    /// <summary>请求体字节数上限（1 字节至 64 MiB），默认 16 MiB；超出时返回 413。</summary>
    public int MaxBodyBytes { get; set; } = 16 * 1024 * 1024;

    /// <summary>每个客户端每个自然分钟（按 UTC 整分钟计）允许的请求次数（1–10000），超出返回 429。</summary>
    public int RequestsPerMinute { get; set; } = 120;

    /// <summary>客户端访问名单；仅用于旧配置迁移和内存夹具，数据库保存的设置里始终为空列表，正式名单由身份仓储管理。</summary>
    public List<ClientAccess> Clients { get; set; } = [];

    /// <summary>校验全部字段的格式与取值范围，并校验 <see cref="Clients"/>；任一不合法即抛出 <see cref="InvalidOperationException"/>，不做自动修正。</summary>
    public void Validate()
    {
        if (ListenPort is < 1024 or > 65535 || AuthMode is not ("oauth" or "api-key")
            || MaxConcurrent is < 1 or > 8 || MaxPerClient is < 1 or > 8 || MaxQueued is < 1 or > 64
            || QueueTimeoutSeconds is < 1 or > 120 || UploadTimeoutSeconds is < 1 or > 120
            || RequestTimeoutSeconds is < 1 or > 3600 || MaxBodyBytes is < 1 or > 67108864
            || RequestsPerMinute is < 1 or > 10000
            || !IsHex(DeviceId, 64) || string.IsNullOrWhiteSpace(UpstreamUserAgent)
            || UpstreamUserAgent.Length > 256 || UpstreamUserAgent.Any(char.IsControl))
            throw new InvalidOperationException("中转配置格式或范围错误。");
        if (UpstreamProxy is not null && (!Uri.TryCreate(UpstreamProxy, UriKind.Absolute, out var proxy)
            || proxy.Scheme is not ("http" or "https"))) throw new InvalidOperationException("上游代理必须是 HTTP/HTTPS 地址。");
        if (ProxyMode is not ("none" or "auto" or "manual")) throw new InvalidOperationException("请选择不使用代理、自动检测系统代理或手动指定地址。");
        if (ProxyMode == "manual" && string.IsNullOrWhiteSpace(ProxyUrl)) throw new InvalidOperationException("已选择手动指定代理，请填写代理地址，例如 http://127.0.0.1:7897。");
        if (!string.IsNullOrWhiteSpace(ProxyUrl)) Upstream.UpstreamAddress.Proxy(ProxyUrl);
        ValidateClients(Clients);
    }

    /// <summary>判断字符串是否恰好为指定长度且全部是十六进制字符（大小写均可）。</summary>
    public static bool IsHex(string value, int length) => value.Length == length && value.All(Uri.IsHexDigit);

    /// <summary>判断是否为合法客户端名：1–32 个字符，以小写字母开头，只含小写字母、数字、下划线和连字符。</summary>
    public static bool IsClientName(string value) => value.Length is > 0 and <= 32
        && value[0] is >= 'a' and <= 'z' && value.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '_' or '-');

    /// <summary>校验客户端名单：最多 8 个，名称合法，令牌哈希为 64 位十六进制，且名称、编号、令牌哈希（不区分大小写）都不重复；否则抛出 <see cref="InvalidOperationException"/>。</summary>
    public static void ValidateClients(IReadOnlyList<ClientAccess> clients)
    {
        if (clients.Count > 8 || clients.Any(c => !IsClientName(c.Name) || !IsHex(c.TokenHash, 64))
            || clients.Select(c => c.Name).Distinct().Count() != clients.Count
            || clients.Select(c => c.Id).Distinct().Count() != clients.Count
            || clients.Select(c => c.TokenHash).Distinct(StringComparer.OrdinalIgnoreCase).Count() != clients.Count)
            throw new InvalidOperationException("客户端配置错误，最多允许 8 个独立令牌。");
    }
}
