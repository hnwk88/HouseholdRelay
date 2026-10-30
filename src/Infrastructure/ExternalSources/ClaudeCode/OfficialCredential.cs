using System.Text.Json;

namespace Infrastructure.ExternalSources.ClaudeCode;

/// <summary>
/// Claude Code 配置目录里 <c>.credentials.json</c> 的 <c>claudeAiOauth</c> 一节中中转要用的：访问令牌、到期时间（文件没写时为 null）与有没有刷新令牌（只记有无，不留它的值）；
/// 用普通类避免自动输出令牌。
/// </summary>
public sealed class OfficialCredential(string accessToken, DateTimeOffset? expiresAt, bool refreshable)
{
    /// <summary>凭据文件名；Claude Code 在 Windows 上把官方登录凭据明文写在配置目录的这个文件里。</summary>
    public const string FileName = ".credentials.json";

    /// <summary>导入的登录文件上限；Claude Code 写的凭据文件只有几 KB，超过的不是它。</summary>
    public const int MaxFileBytes = 64 * 1024;

    /// <summary>当前访问令牌；只用于发往上游的 Authorization 头和脱敏。</summary>
    public string AccessToken { get; } = accessToken;

    /// <summary>令牌到期时间（文件里的 <c>expiresAt</c> 毫秒时间戳）；没有时为 null，此时不主动刷新。</summary>
    public DateTimeOffset? ExpiresAt { get; } = expiresAt;

    /// <summary>文件里有非空的 <c>claudeAiOauth.refreshToken</c>；没有时 CLI 无法续期，导入时拒绝。</summary>
    public bool Refreshable { get; } = refreshable;

    /// <summary>
    /// 解析凭据文件正文：没有 <c>claudeAiOauth.accessToken</c>、令牌为空或含空白与控制字符时返回 null；正文不是 JSON 时抛出 <see cref="JsonException"/>，交给调用方决定是否重读。
    /// </summary>
    public static OfficialCredential? Parse(byte[] content)
    {
        using var document = JsonDocument.Parse(content);
        if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("claudeAiOauth", out var oauth)
            || oauth.ValueKind != JsonValueKind.Object || !oauth.TryGetProperty("accessToken", out var token) || token.ValueKind != JsonValueKind.String) return null;
        var value = token.GetString()!;
        if (value.Length is 0 or > 8192 || value.Any(char.IsWhiteSpace) || value.Any(char.IsControl)) return null;
        DateTimeOffset? expires = oauth.TryGetProperty("expiresAt", out var at) && at.ValueKind == JsonValueKind.Number && at.TryGetInt64(out var milliseconds)
            ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds) : null;
        var refreshable = oauth.TryGetProperty("refreshToken", out var refresh) && refresh.ValueKind == JsonValueKind.String && refresh.GetString()!.Trim().Length > 0;
        return new(value, expires, refreshable);
    }
}
