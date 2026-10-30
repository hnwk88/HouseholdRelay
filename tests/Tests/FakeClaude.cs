using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Application.Interfaces.ClaudeCode;
using Infrastructure.ExternalSources.ClaudeCode;
using Microsoft.Extensions.DependencyInjection;

namespace Tests;

/// <summary>
/// 假的 Claude Code 命令行：测试程序以 <c>--fake-claude 场景 参数…</c> 运行时扮演 claude，只在 <c>CLAUDE_CONFIG_DIR</c> 里读写，绝不碰真实账户。
/// 每次调用把命令行与看到的环境记进配置目录，供测试核对隔离是否到位。
/// </summary>
static class FakeClaude
{
    /// <summary>假登录报告的邮箱。</summary>
    public const string Email = "family.member@example.com";
    /// <summary>假登录写入的第一个访问令牌。</summary>
    public const string FirstToken = "fixture-login-token-first";
    /// <summary>假刷新（<c>-p /usage</c>）写入的新访问令牌。</summary>
    public const string RefreshedToken = "fixture-login-token-refreshed";

    /// <summary>
    /// 按场景扮演 claude：<c>auth login</c> 在 success 下写入一小时后到期的令牌、expired 下写入已过期的令牌、no-credential 下什么也不写，cancel 下以退出码 1 结束；
    /// <c>auth status --json</c> 报告已登录与邮箱；<c>-p /usage</c> 写入刷新后的新令牌（slow-refresh 下先卡 30 秒，模拟刷新卡住）。返回退出码。
    /// </summary>
    public static int Run(string[] args)
    {
        var scenario = args[1]; var command = string.Join(' ', args.Skip(2));
        var directory = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") ?? throw new InvalidOperationException("CLAUDE_CONFIG_DIR missing");
        File.AppendAllText(Path.Combine(directory, "fake-calls.log"), command + "\n");
        var inherited = Environment.GetEnvironmentVariables().Keys.Cast<string>()
            .Where(key => key.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("CLAUDE_", StringComparison.OrdinalIgnoreCase) && key is not ("CLAUDE_CONFIG_DIR" or "CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC")).ToArray();
        File.WriteAllText(Path.Combine(directory, "fake-env.json"), JsonSerializer.Serialize(new { inherited, directory, noProxy = Environment.GetEnvironmentVariable("NO_PROXY") }));
        // 每次调用各记一行有没有可见的控制台窗口，浏览器旅程据此核对登录子进程没有在用户屏幕上出窗口。
        var window = GetConsoleWindow();
        File.AppendAllText(Path.Combine(directory, "fake-windows.log"), $"{command}\t{window != IntPtr.Zero && IsWindowVisible(window)}\n");
        if (command == "auth login")
        {
            if (scenario == "cancel") return 1;
            if (scenario != "no-credential") Write(directory, FirstToken, scenario == "expired" ? -1 : 60);
            return 0;
        }
        if (command == "auth status --json") { Console.WriteLine(JsonSerializer.Serialize(new { loggedIn = true, authMethod = "claude.ai", email = Email })); return 0; }
        if (command.StartsWith("-p /usage", StringComparison.Ordinal))
        {
            if (scenario == "slow-refresh") Thread.Sleep(TimeSpan.FromSeconds(30));
            Write(directory, RefreshedToken, 60); Console.WriteLine("{}"); return 0;
        }
        return 2;
    }

    /// <summary>
    /// 以 claude.exe 之名运行时的入口：浏览器旅程把测试程序复制成 claude.exe 放进子服务的 PATH，真定位器就会找到它。
    /// 场景从 <see cref="ScenarioFileVariable"/> 指向的文件读，旅程改写这个文件就能在同一个子服务里先后走取消与成功；没有这个变量时拒绝扮演。
    /// </summary>
    public static int RunAsClaude(string[] args)
    {
        if (Environment.GetEnvironmentVariable(ScenarioFileVariable) is not { Length: > 0 } file) { Console.Error.WriteLine(ScenarioFileVariable + " missing"); return 2; }
        return Run(["--fake-claude", File.ReadAllText(file).Trim(), .. args]);
    }

    /// <summary>浏览器旅程用来告诉假 claude.exe 场景文件位置的环境变量；不以 CLAUDE_ 或 ANTHROPIC_ 开头，隔离环境不会清掉它。</summary>
    public const string ScenarioFileVariable = "HOUSEHOLD_FAKE_CLAUDE_SCENARIO_FILE";

    /// <summary>取本进程所属控制台的窗口；没有窗口（以不建窗口方式启动）时返回零。</summary>
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    /// <summary>窗口是否可见；用来核对登录子进程没有在用户屏幕上出窗口。</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    /// <summary>按 Claude Code 的格式写凭据文件：<c>claudeAiOauth</c> 下的访问令牌、刷新令牌和 <paramref name="minutes"/> 分钟后到期的毫秒时间戳。</summary>
    public static void Write(string directory, string token, int minutes) => File.WriteAllBytes(Path.Combine(directory, OfficialCredential.FileName), Credential(token, minutes));

    /// <summary>按 Claude Code 的格式生成凭据文件正文；<paramref name="refreshToken"/> 为 null 时不写刷新令牌（导入时应被拒）。</summary>
    public static byte[] Credential(string token, int minutes, string? refreshToken = "fixture-refresh-token") => JsonSerializer.SerializeToUtf8Bytes(new
    {
        claudeAiOauth = new { accessToken = token, refreshToken, expiresAt = DateTimeOffset.UtcNow.AddMinutes(minutes).ToUnixTimeMilliseconds(), scopes = new[] { "user:inference" } }
    });
}

/// <summary>
/// 指向假 Claude Code 的定位器：<see cref="FindClaude"/> 返回一个临时 <c>claude.cmd</c>，它以 <c>--fake-claude 场景</c> 调回本测试程序；场景为 missing 时报告未安装。
/// 同时把登录改成不建窗口，测试不在用户屏幕上出任何窗口。
/// </summary>
sealed class FakeClaudeLauncher(string scenario) : IClientLauncher, IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("hr-fake-claude-").FullName;

    /// <summary>把本定位器与不建窗口的登录参数登记进网页主机，覆盖正式登记。</summary>
    public void Register(IServiceCollection services)
    {
        services.AddSingleton<IClientLauncher>(this);
        services.AddSingleton(new OfficialLoginOptions { ShowWindow = false, LoginTimeout = TimeSpan.FromSeconds(30) });
    }

    /// <summary>返回假 claude 的路径；场景为 missing 时照真定位器的写法抛出未安装。</summary>
    public string FindClaude()
    {
        if (scenario == "missing") throw new InvalidOperationException("未找到 Claude Code 原生 claude.exe，请先安装官方客户端。");
        var path = Path.Combine(directory, "claude.cmd");
        if (!File.Exists(path))
        {
            var host = Environment.ProcessPath!;
            var command = Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                ? $"\"{host}\" \"{typeof(FakeClaude).Assembly.Location}\"" : $"\"{host}\"";
            File.WriteAllText(path, $"@{command} --fake-claude {scenario} %*\r\n");
        }
        return path;
    }

    /// <summary>登录测试用不到。</summary>
    public Task<string> DetectUserAgent() => Task.FromResult("claude-cli/0.0.0 (external, cli)");

    /// <summary>登录测试用不到。</summary>
    public ProcessStartInfo Prepare(string bundleDirectory, IEnumerable<string> arguments) => throw new NotSupportedException();

    /// <summary>登录测试用不到。</summary>
    public Task<int> Run(string directory, string[] arguments) => throw new NotSupportedException();

    /// <summary>删除临时的 claude.cmd 所在目录。</summary>
    public void Dispose()
    {
        // 只删本定位器自己在系统临时目录下建的那个目录；假进程还没完全退出占着文件时放弃，残留的只是一个小脚本。
        try { Directory.Delete(directory, true); }
        // 文件仍被占用：放弃清理，不让测试因此失败。
        catch (IOException) { }
    }
}
