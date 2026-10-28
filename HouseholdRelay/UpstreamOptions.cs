using System.Text.Json.Serialization;

namespace HouseholdRelay;

/// <summary>一个候选版本的非秘密配置；地址和出站代理分别建模。</summary>
public sealed record UpstreamOptions
{
    public string Mode { get; init; } = "api-key";
    public string Authentication { get; init; } = "x-api-key";
    public string BaseUrl { get; init; } = "https://api.anthropic.com";
    public bool OAuthBeta { get; init; }
    public string Preset { get; init; } = "anthropic";
    public bool AllowPrivateNetwork { get; init; }
    public string? ProxyUrl { get; init; }
    public string Model { get; init; } = "";
    public string Diagnostic { get; init; } = "messages";

    public UpstreamOptions Validated()
    {
        if (Diagnostic is not ("messages" or "claude-code")) throw new InvalidOperationException("请选择支持的调用检测方式。");
        if (Preset is not ("anthropic" or "compatible") || Mode is not ("oauth" or "api-key") || Authentication is not ("bearer" or "x-api-key")
            || Mode == "oauth" && Authentication != "bearer" || Mode == "api-key" && OAuthBeta)
            throw new InvalidOperationException("账户模式使用 Bearer；API 模式不可附加账户 OAuth beta。");
        if (Model.Length > 200 || Model.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or '/' or ':')))
            throw new InvalidOperationException("模型名称只能包含字母、数字和 - _ . / :，最多 200 字符。");
        var address = UpstreamAddress.Normalize(BaseUrl, AllowPrivateNetwork);
        var proxy = string.IsNullOrWhiteSpace(ProxyUrl) ? null : UpstreamAddress.Proxy(ProxyUrl).AbsoluteUri.TrimEnd('/');
        return this with { BaseUrl = address.Query.Length == 0 ? address.AbsoluteUri.TrimEnd('/') : address.AbsoluteUri, ProxyUrl = proxy,
            OAuthBeta = Mode == "oauth" && (Preset == "anthropic" || OAuthBeta) };
    }

    internal static UpstreamOptions Legacy(RelayConfiguration config, string baseUrl = "https://api.anthropic.com") => new()
    { Mode = config.AuthMode, Authentication = config.AuthMode == "oauth" ? "bearer" : "x-api-key",
        OAuthBeta = config.AuthMode == "oauth", BaseUrl = baseUrl, ProxyUrl = config.UpstreamProxy };
}

/// <summary>仅保存安全的检测状态；不保存上游原始响应或凭据。</summary>
public sealed record UpstreamReport(DateTimeOffset CheckedAt, string Connection, string Authentication, string ModelDiscovery,
    string Call, string[] Models, string? VerifiedModel, string Message)
{
    [JsonIgnore] public bool CanActivate => Call == "passed";
    public static string Label(string status) => status switch
    {
        "passed" => "通过", "failed" => "失败", "unsupported" => "未提供模型目录", "unverified" => "未检测",
        "reconnect" => "凭据失效，需重新接入", "cooldown" => "请求过多，稍后重试", "cancelled" => "检测已取消", _ => "未检测"
    };
}

/// <summary>可向管理页面展示的候选；不含任何秘密。</summary>
public sealed record UpstreamCandidate(string Id, long ExpectedVersion, UpstreamOptions Options, string State, UpstreamReport? Report);

/// <summary>请求持有的不可变活动版本；避免记录类型自动输出凭据。</summary>
public sealed class UpstreamSnapshot(long version, UpstreamOptions options, string? credential, UpstreamReport? report = null)
{
    public long Version { get; } = version;
    public UpstreamOptions Options { get; } = options;
    internal string? Credential { get; } = credential;
    public bool Configured => Credential is not null;
    public UpstreamReport? Report { get; } = report;
}
