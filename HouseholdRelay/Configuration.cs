using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HouseholdRelay;

public sealed record ClientAccess(string Name, string TokenHash, bool Enabled = true);
public sealed record ClientBundle(string BaseUrl, string Token);

public sealed class RelayConfiguration
{
    public int ListenPort { get; set; } = 8787;
    public string PublicUrl { get; set; } = "";
    public string AuthMode { get; set; } = "oauth";
    public string DeviceId { get; set; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    public string UpstreamUserAgent { get; set; } = "";
    public bool NormalizeDeviceMetadata { get; set; } = true;
    public string? UpstreamProxy { get; set; }
    public int MaxConcurrent { get; set; } = 2;
    public int MaxPerClient { get; set; } = 1;
    public int MaxQueued { get; set; } = 16;
    public int QueueTimeoutSeconds { get; set; } = 30;
    public int UploadTimeoutSeconds { get; set; } = 30;
    public int RequestTimeoutSeconds { get; set; } = 600;
    public int MaxBodyBytes { get; set; } = 16 * 1024 * 1024;
    public int RequestsPerMinute { get; set; } = 120;
    public List<ClientAccess> Clients { get; set; } = [];

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
        ValidateClients(Clients);
    }

    public static bool IsHex(string value, int length) => value.Length == length && value.All(Uri.IsHexDigit);
    public static bool IsClientName(string value) => value.Length is > 0 and <= 32
        && value[0] is >= 'a' and <= 'z' && value.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '_' or '-');
    public static void ValidateClients(IReadOnlyList<ClientAccess> clients)
    {
        if (clients.Count is < 1 or > 8 || clients.Any(c => !IsClientName(c.Name) || !IsHex(c.TokenHash, 64))
            || clients.Select(c => c.Name).Distinct().Count() != clients.Count
            || clients.Select(c => c.TokenHash).Distinct(StringComparer.OrdinalIgnoreCase).Count() != clients.Count)
            throw new InvalidOperationException("客户端配置错误，最多允许 8 个独立令牌。");
    }
}

public static class ConfigurationFiles
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    public static string NewToken() => "hr_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json)
        ?? throw new InvalidOperationException("配置文件为空。");
    public static void Write<T>(string path, T value)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Json) + Environment.NewLine, new UTF8Encoding(false));
        File.Move(temp, path, true);
    }
}
