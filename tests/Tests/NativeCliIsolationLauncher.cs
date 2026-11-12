using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Infrastructure.ExternalSources.ClaudeCode;

namespace Tests;

/// <summary>真 CLI 测试专用定位器；只有本次复制程序及已核验防火墙回执可用，绝不回退到个人 Claude。</summary>
sealed class NativeCliIsolationLauncher : IClientLauncher
{
    private const string Variable = "HOUSEHOLD_NATIVE_CLAUDE";
    private readonly IClientLauncher original = new ClientLauncher();

    /// <summary>核对受控环境变量、临时目录形状和规则回执；缺少隔离时在创建进程之前拒绝。</summary>
    public string FindClaude()
    {
        var provided = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrWhiteSpace(provided)) throw new InvalidOperationException("真 CLI 尚未建立专用复制程序与防火墙隔离；请使用 --isolated-mocks 或先准备本次隔离。");
        var path = Path.GetFullPath(provided); var directory = Path.GetDirectoryName(path)!;
        var name = Path.GetFileName(directory); const string Prefix = "HouseholdRelay-native-isolation-";
        if (Path.GetFileName(path) != "claude.exe" || !name.StartsWith(Prefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(name[Prefix.Length..], "N", out var owner)
            || !string.Equals(Path.GetDirectoryName(directory), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), StringComparison.OrdinalIgnoreCase)
            || !File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
            || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("真 CLI 程序不在本次测试专用临时目录，未启动。");
        using var receipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "firewall-receipt.json")));
        var root = receipt.RootElement;
        if (!root.GetProperty("ready").GetBoolean() || root.GetProperty("owner").GetString() != owner.ToString("N", CultureInfo.InvariantCulture)
            || !string.Equals(root.GetProperty("executable").GetString(), path, StringComparison.OrdinalIgnoreCase)
            || root.GetProperty("rules").GetArrayLength() != 2)
            throw new InvalidOperationException("真 CLI 隔离规则尚未就绪或已经移除，未启动。");
        using var program = File.OpenRead(path);
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(program)), root.GetProperty("executable_sha256").GetString(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("真 CLI 专用副本已改变，必须重新核验隔离后才能启动。");
        return path;
    }

    /// <summary>版本查询也只在复制程序和一次性配置中进行，不继承宿主登录环境。</summary>
    public async Task<string> DetectUserAgent()
    {
        using var temporary = new TestHome();
        var start = new ProcessStartInfo(FindClaude()) { WorkingDirectory = temporary.Path, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        IsolatedClaudeEnvironment.Apply(start, temporary.Path, new ProxyRoute(null, "localhost,127.0.0.1,::1", "测试仅回环"));
        start.ArgumentList.Add("--version");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动隔离版本查询。");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token); await process.WaitForExitAsync(timeout.Token);
            var version = Regex.Match(output, @"\b\d+\.\d+\.\d+\b");
            if (process.ExitCode != 0 || !version.Success) throw new InvalidOperationException("隔离客户端版本查询失败。");
            return $"claude-cli/{version.Value} (external, cli)";
        }
        // 超时和失败均结束本次复制程序，不留下失去规则保护后还在运行的子进程。
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }

    /// <summary>沿用客户端包协议后替换为专用程序，并清除全部代理，防止经本机个人代理绕过非回环规则。</summary>
    public ProcessStartInfo Prepare(string bundleDirectory, IEnumerable<string> arguments)
    {
        var executable = FindClaude(); var start = original.Prepare(bundleDirectory, arguments); start.FileName = executable;
        ChildProxyEnvironment.Apply(start, new ProxyRoute(null, "localhost,127.0.0.1,::1", "测试仅回环"));
        return start;
    }

    /// <summary>测试通过上述受控参数启动，等待整个 CLI 退出后才归还。</summary>
    public async Task<int> Run(string directory, string[] arguments)
    {
        using var process = Process.Start(Prepare(directory, arguments)) ?? throw new InvalidOperationException("无法启动隔离客户端。");
        await process.WaitForExitAsync(); return process.ExitCode;
    }
}
