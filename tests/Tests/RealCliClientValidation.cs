using System.Diagnostics;
using System.Text.Json.Nodes;
using Infrastructure.ExternalSources.ClaudeCode;

namespace Tests;

/// <summary>仅显式调用时，在伪控制台里以交互模式（入口 cli）跑一次真 Claude Code 客户端，经中转发一条「Reply with OK.」；不开窗口，不输出令牌。</summary>
static class RealCliClientValidation
{
    /// <summary>
    /// 命令行首参为 --real-cli-client 时运行：参数依次为 claude.exe、中转地址、令牌文件、中转根证书、模型、观察文件、超时秒数。
    /// 在新建的临时目录里预写首次运行设置（照中转抓 cli 身份的写法），清掉宿主的 Claude 与 Anthropic 变量后起交互模式；
    /// 观察文件的行数增加（中转一侧看到请求）后再等 5 秒结束进程树，到超时也结束。返回 0 表示看到了新记录，2 表示超时。令牌只从文件读，读完不打印。
    /// </summary>
    public static async Task<int> Run(string[] args)
    {
        if (args.Length != 8) throw new ArgumentException("需要 claude.exe、中转地址、令牌文件、根证书、模型、观察文件和超时秒数。");
        var (claude, baseUrl, tokenFile, ca, model, watch) = (args[1], args[2], args[3], args[4], args[5], args[6]);
        var timeout = TimeSpan.FromSeconds(int.Parse(args[7], System.Globalization.CultureInfo.InvariantCulture));
        var token = File.ReadAllText(tokenFile).Trim();
        var root = Directory.CreateTempSubdirectory("hr-cli-client-").FullName;
        var work = Path.Combine(root, "work"); var config = Path.Combine(root, "config");
        Directory.CreateDirectory(work); Directory.CreateDirectory(config);
        var trusted = new JsonObject { ["hasTrustDialogAccepted"] = true, ["hasCompletedProjectOnboarding"] = true };
        File.WriteAllText(Path.Combine(config, ".claude.json"), new JsonObject { ["hasCompletedOnboarding"] = true, ["theme"] = "dark",
            ["projects"] = new JsonObject { [work] = trusted.DeepClone(), [work.Replace('\\', '/')] = trusted.DeepClone() } }.ToJsonString());
        var start = new ProcessStartInfo(claude) { WorkingDirectory = work, UseShellExecute = false };
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("CLAUDE", StringComparison.OrdinalIgnoreCase)
            || key is "NODE_EXTRA_CA_CERTS" or "SSL_CERT_FILE" or "NODE_TLS_REJECT_UNAUTHORIZED" or "HTTPS_PROXY" or "HTTP_PROXY" or "ALL_PROXY" or "NO_PROXY").ToArray()) start.Environment.Remove(key);
        start.Environment["CLAUDE_CONFIG_DIR"] = config; start.Environment["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";
        start.Environment["ANTHROPIC_BASE_URL"] = baseUrl; start.Environment["ANTHROPIC_AUTH_TOKEN"] = token;
        start.Environment["NODE_EXTRA_CA_CERTS"] = ca; start.Environment["NO_PROXY"] = "*";
        foreach (var argument in new[] { "--model", model, "--strict-mcp-config", "--setting-sources", "", "Reply with OK." }) start.ArgumentList.Add(argument);
        var before = Lines(watch); var seen = false;
        using (var terminal = PseudoConsoleProcess.Start(start, new Dictionary<string, string> { ["Press Enter to continue"] = "\r" }))
        {
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < timeout && !terminal.Process.HasExited)
            {
                if (Lines(watch) > before) { seen = true; await Task.Delay(TimeSpan.FromSeconds(5)); break; }
                await Task.Delay(500);
            }
        }
        // 只删本次在系统临时目录下新建的那个目录；子进程刚结束还占着文件时放弃。
        try { Directory.Delete(root, true); }
        // 文件仍被占用：残留的只是本次临时配置。
        catch (IOException) { }
        Console.WriteLine($"cli client: request_seen={seen}; new_records={Lines(watch) - before}");
        return seen ? 0 : 2;
    }

    /// <summary>观察文件当前的行数；文件还没有时为 0。</summary>
    private static int Lines(string path) => File.Exists(path) ? File.ReadLines(path).Count() : 0;
}
