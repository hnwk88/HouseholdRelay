using System.Text.Json;
using System.Text.RegularExpressions;

namespace Core.Upstream;

/// <summary>
/// 发来请求的那台客户端 Claude Code 自报的版本与入口：先读正文 <c>system[0]</c> 的计费头 <c>cc_version=版本.后缀; cc_entrypoint=入口;</c>（这是官方在正文里看到的），
/// 没有时读客户端 User-Agent 的 <c>claude-cli/版本 (external, 入口</c>；两处都没有的项为 null。只读不改。
/// </summary>
public sealed partial record ClaudeCodeClient(string? Version, string? Entrypoint)
{
    /// <summary>请求里看不出入口时用的入口：中转自己发起的请求（获取模型、测试）都是 <c>--print</c> 跑出来的。</summary>
    public const string DefaultEntrypoint = "sdk-cli";

    /// <summary>能从本机真 claude.exe 实抓到身份的入口：cli 用交互模式，其余用 <c>--print</c>（非 sdk-cli 的再设 CLAUDE_CODE_ENTRYPOINT）。别的入口不抓，按 <see cref="DefaultEntrypoint"/> 发。</summary>
    public static readonly IReadOnlySet<string> CapturableEntrypoints = new HashSet<string>(StringComparer.Ordinal) { "cli", "sdk-cli", "sdk-ts", "sdk-py", "claude-vscode" };

    /// <summary>这次请求该用哪个入口的统一身份：客户端自报的入口能实抓就用它，否则用 <see cref="DefaultEntrypoint"/>。</summary>
    public string IdentityEntrypoint => Entrypoint is { } entrypoint && CapturableEntrypoints.Contains(entrypoint) ? entrypoint : DefaultEntrypoint;

    /// <summary>从请求正文与客户端 User-Agent 读出版本与入口；正文不是 JSON 对象或没有计费头时退回 User-Agent，都读不出的项为 null。</summary>
    public static ClaudeCodeClient Read(byte[] body, string? userAgent)
    {
        string? version = null, entrypoint = null;
        if (Billing(body) is { } billing && BillingPattern().Match(billing) is { Success: true } found)
        { version = found.Groups[1].Value; entrypoint = found.Groups[2].Value; }
        if (AgentPattern().Match(userAgent ?? "") is { Success: true } agent)
        { version ??= agent.Groups[1].Value; entrypoint ??= agent.Groups[2].Value; }
        return new(version, entrypoint);
    }

    /// <summary>按「主.次.修订」三段数字比较两个版本：前者新返回正数、旧返回负数、相同返回 0；任一个不是这种格式时返回 null。</summary>
    public static int? Compare(string? left, string? right)
    {
        if (Parse(left) is not { } a || Parse(right) is not { } b) return null;
        return a.CompareTo(b);
    }

    /// <summary>把「主.次.修订」读成 <see cref="System.Version"/>；格式不对时为 null。</summary>
    private static Version? Parse(string? value) =>
        value is not null && VersionPattern().IsMatch(value) && System.Version.TryParse(value, out var parsed) ? parsed : null;

    /// <summary>取正文 <c>system</c> 数组第一段文字的首行（计费头所在处）；正文不是 JSON 对象、没有 system 或第一段不是文字时为 null。</summary>
    private static string? Billing(byte[] body)
    {
        if (body.Length == 0) return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("system", out var system)) return null;
            var first = system.ValueKind == JsonValueKind.Array && system.GetArrayLength() > 0 ? system[0] : system;
            var text = first.ValueKind == JsonValueKind.String ? first.GetString()
                : first.ValueKind == JsonValueKind.Object && first.TryGetProperty("text", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            return text?.Split('\n', 2)[0];
        }
        // 正文不是合法 JSON：没有计费头可读，退回 User-Agent。
        catch (JsonException) { return null; }
    }

    /// <summary>计费头：第 1 组版本、第 2 组入口；后缀只认 Claude Code 生成的 3 位十六进制。</summary>
    [GeneratedRegex(@"^x-anthropic-billing-header: cc_version=(\d+\.\d+\.\d+)\.[0-9a-f]{3}; cc_entrypoint=([a-z][a-z0-9_-]{0,31});", RegexOptions.CultureInvariant)]
    private static partial Regex BillingPattern();

    /// <summary>客户端 User-Agent：第 1 组版本、第 2 组入口。</summary>
    [GeneratedRegex(@"^claude-cli/(\d+\.\d+\.\d+) \(external, ([a-z][a-z0-9_-]{0,31})", RegexOptions.CultureInvariant)]
    private static partial Regex AgentPattern();

    /// <summary>「主.次.修订」三段数字。</summary>
    [GeneratedRegex(@"^\d{1,6}\.\d{1,6}\.\d{1,6}$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}
