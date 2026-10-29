using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Application.Interfaces.ClaudeCode;
using Core.Configuration;

namespace Infrastructure.ExternalSources.ClaudeCode;

/// <summary>定位本机原生 Claude Code，并以只对子进程生效的连接变量和信任证书启动它。</summary>
internal sealed partial class ClientLauncher : IClientLauncher
{
    /// <summary>
    /// 依次在 PATH 各目录、<c>%USERPROFILE%\.local\bin</c>、<c>%APPDATA%\npm</c> 里找 <c>claude.exe</c>，每个目录同时找 npm 全局安装的
    /// <c>node_modules\@anthropic-ai\claude-code\bin\claude.exe</c>；找不到时抛出 <see cref="InvalidOperationException"/> 提示安装。
    /// </summary>
    public string FindClaude()
    {
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(p => p.Trim('"')).Where(Directory.Exists).ToList();
        directories.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"));
        directories.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
        foreach (var directory in directories.Distinct())
        {
            foreach (var relative in new[] { "claude.exe", "node_modules/@anthropic-ai/claude-code/bin/claude.exe" })
            {
                var candidate = Path.GetFullPath(Path.Combine(directory, relative));
                if (File.Exists(candidate)) return candidate;
            }
        }
        throw new InvalidOperationException("未找到 Claude Code 原生 claude.exe，请先安装官方客户端。");
    }

    /// <summary>读取本机客户端版本并组成请求特征。</summary>
    public async Task<string> DetectUserAgent()
    {
        var start = new ProcessStartInfo(FindClaude()) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add("--version");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 Claude Code。");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token); await process.WaitForExitAsync(timeout.Token);
            var version = VersionPattern().Match(output);
            if (!version.Success) throw new InvalidOperationException("无法识别 Claude Code 版本。");
            return $"claude-cli/{version.Value} (external, cli)";
        }
        // 超时、版本识别失败或读取出错时都要结束卡住的子进程树，避免查询版本留下后台进程。
        finally { if (!process.HasExited) process.Kill(true); }
    }

    /// <summary>按旧客户端包准备子进程启动参数。</summary>
    public ProcessStartInfo Prepare(string bundleDirectory, IEnumerable<string> arguments)
    {
        var bundle = ConfigurationFiles.Read<ClientBundle>(Path.Combine(bundleDirectory, "client.json"));
        if (!Uri.TryCreate(bundle.BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !bundle.Token.StartsWith("hr_"))
            throw new InvalidOperationException("客户端配置错误。");
        var certificate = Path.Combine(bundleDirectory, "relay-ca.pem");
        if (!File.Exists(certificate)) throw new InvalidOperationException("客户端包缺少 HTTPS 信任证书。");
        var state = Path.Combine(bundleDirectory, ".claude-state"); Directory.CreateDirectory(state);
        var start = new ProcessStartInfo(FindClaude()) { UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        foreach (var key in new[] { "ANTHROPIC_API_KEY", "CLAUDE_CODE_OAUTH_TOKEN", "ANTHROPIC_CUSTOM_HEADERS", "CLAUDE_CODE_USE_BEDROCK",
            "CLAUDE_CODE_USE_VERTEX", "CLAUDE_CODE_USE_FOUNDRY", "CLAUDE_CODE_USE_AWS" }) start.Environment.Remove(key);
        start.Environment["ANTHROPIC_BASE_URL"] = bundle.BaseUrl;
        start.Environment["ANTHROPIC_AUTH_TOKEN"] = bundle.Token;
        start.Environment["CLAUDE_CONFIG_DIR"] = state;
        start.Environment["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";
        // 家人客户端跑在家人自己的电脑上，中转的全局代理设置管不到这里；它自己的代理（联网工具可能要用）原样保留，不照 ChildProxyEnvironment 先清后写，
        // 只把中转主机加进 NO_PROXY 让它直连。Windows 的环境变量名不分大小写，NO_PROXY 与 no_proxy 是同一项。
        start.Environment.TryGetValue("NO_PROXY", out var noProxy);
        start.Environment["NO_PROXY"] = string.Join(',', $"{noProxy},{uri.Host}".Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase));
        // 信任证书只对这个子进程生效，绝不关闭证书校验。
        if (start.Environment.TryGetValue("NODE_EXTRA_CA_CERTS", out var existing) && File.Exists(existing))
        {
            var combined = Path.Combine(state, "combined-ca.pem");
            File.WriteAllText(combined, File.ReadAllText(existing) + "\n" + File.ReadAllText(certificate), Encoding.ASCII); certificate = combined;
        }
        start.Environment["NODE_EXTRA_CA_CERTS"] = certificate;
        return start;
    }

    /// <summary>启动客户端并返回退出码。</summary>
    public async Task<int> Run(string directory, string[] arguments)
    {
        using var process = Process.Start(Prepare(directory, arguments)) ?? throw new InvalidOperationException("客户端启动失败。");
        await process.WaitForExitAsync(); return process.ExitCode;
    }

    /// <summary>编译期生成的版本号匹配式，从 <c>claude --version</c> 输出中取出第一个「主.次.修订」三段数字。</summary>
    [GeneratedRegex(@"\b\d+\.\d+\.\d+\b", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}
