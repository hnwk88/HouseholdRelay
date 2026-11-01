using System.Text.RegularExpressions;

namespace Core.Upstream;

/// <summary>
/// 中转所在电脑上真 Claude Code 发请求时带的身份头（User-Agent、<c>x-stainless-*</c>、<c>x-app</c> 等），取自本机 claude.exe 实际发出的一次请求；
/// 按入口各取一份（交互的 cli、<c>--print</c> 的 sdk-cli 等），发往官方账户的请求按正文里的入口带对应那一份，家里每台客户端自己的标识一律换掉。<paramref name="Version"/> 与 <paramref name="Entrypoint"/> 读自 User-Agent。
/// </summary>
public sealed partial record ClaudeCodeIdentity(IReadOnlyList<KeyValuePair<string, string>> Headers, string Version, string Entrypoint, DateTimeOffset CapturedAt)
{
    /// <summary>本机找不到 Claude Code 时的读句。</summary>
    public const string NotFound = "中转所在电脑找不到 Claude Code，这次没有发往官方账户。请在中转所在电脑重新安装 Claude Code 后重试。";
    /// <summary>本机 Claude Code 起来了却没取到请求身份时的读句。</summary>
    public const string CaptureFailed = "没能从中转所在电脑的 Claude Code 取到它的请求身份，这次没有发往官方账户。请在中转所在电脑确认 Claude Code 能正常运行，或重新安装后重试。";
    /// <summary>取到的身份缺了必有的几项时的读句。</summary>
    public const string Incomplete = "从中转所在电脑的 Claude Code 取到的请求身份不全，这次没有发往官方账户。请在中转所在电脑重新安装 Claude Code 后重试。";
    /// <summary>取到的身份与本机 claude.exe 的版本对不上时的读句。</summary>
    public const string Mismatch = "从中转所在电脑的 Claude Code 取到的请求身份与它的版本对不上，这次没有发往官方账户。请在中转所在电脑重新安装 Claude Code 后重试。";
    /// <summary>发往官方账户的请求没有带上统一身份时的读句；正常流程不会走到这里，是最后一道守门。</summary>
    public const string Missing = "没有取到中转所在电脑 Claude Code 的请求身份，这次没有发往官方账户。请稍后重试；一直这样请在中转所在电脑重新安装 Claude Code。";

    /// <summary>不算身份的头：认证、传输与逐跳头，以及跟着这一次请求走的协议版本、beta、内容类型和会话编号（会话编号与正文 <c>metadata.user_id</c> 里的会话一致，按客户端原样保留）。</summary>
    private static readonly HashSet<string> NotIdentity = new(StringComparer.OrdinalIgnoreCase)
    { "Authorization", "X-Api-Key", "Cookie", "Host", "Content-Length", "Accept-Encoding", "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization",
        "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Expect", "anthropic-version", "anthropic-beta", "Content-Type", "X-Claude-Code-Session-Id" };

    /// <summary>官方账户模式下仍按客户端原样带上去的请求头：协议版本、内容类型和会话编号；它们说的是这一次请求，不是哪台设备。anthropic-beta 另按账户设置处理。</summary>
    public static readonly IReadOnlySet<string> RequestHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "anthropic-version", "Content-Type", "X-Claude-Code-Session-Id" };

    /// <summary>
    /// 从本机 Claude Code 实际发出的请求头里取出身份：去掉不算身份的头，其余照收。必须有以 <c>claude-cli/</c> 开头的 User-Agent、<c>x-app</c> 和至少一个 <c>x-stainless-*</c>，
    /// 否则按「取不全」抛 <see cref="ClaudeCodeIdentityUnavailableException"/>；<paramref name="fileVersion"/> 是 claude.exe 的文件版本（没有版本资源时为 null），
    /// 有时 User-Agent 里的版本必须与它的前三段相同；User-Agent 里的入口必须等于要抓的 <paramref name="entrypoint"/>。两样有一样不符就按「对不上」抛出。
    /// </summary>
    public static ClaudeCodeIdentity From(IEnumerable<KeyValuePair<string, string>> captured, string? fileVersion, string entrypoint, DateTimeOffset capturedAt)
    {
        var headers = captured.Where(header => !NotIdentity.Contains(header.Key) && !header.Value.Any(char.IsControl)).ToArray();
        string? Value(string name) => headers.FirstOrDefault(header => header.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
        var agent = AgentVersion().Match(Value("User-Agent") ?? "");
        if (!agent.Success || string.IsNullOrWhiteSpace(Value("x-app")) || !headers.Any(header => header.Key.StartsWith("x-stainless-", StringComparison.OrdinalIgnoreCase)))
            throw new ClaudeCodeIdentityUnavailableException(Incomplete);
        if (fileVersion is not null && FileVersion().Match(fileVersion) is { Success: true } file && file.Value != agent.Groups[1].Value || agent.Groups[2].Value != entrypoint)
            throw new ClaudeCodeIdentityUnavailableException(Mismatch);
        return new(headers, agent.Groups[1].Value, agent.Groups[2].Value, capturedAt);
    }

    /// <summary>这份身份里有没有名为 <paramref name="name"/> 的头（大小写不敏感）。</summary>
    public bool Has(string name) => Headers.Any(header => header.Key.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>匹配 Claude Code 的 User-Agent 开头 <c>claude-cli/主.次.修订 (external, 入口</c>，第 1 组是版本号，第 2 组是入口。</summary>
    [GeneratedRegex(@"^claude-cli/(\d+\.\d+\.\d+) \(external, ([a-z][a-z0-9_-]{0,31})", RegexOptions.CultureInvariant)]
    private static partial Regex AgentVersion();

    /// <summary>取文件版本的前三段数字。</summary>
    [GeneratedRegex(@"^\d+\.\d+\.\d+", RegexOptions.CultureInvariant)]
    private static partial Regex FileVersion();
}
