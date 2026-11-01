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
        // 真 Claude Code 自己建配置目录；检测与抓身份给的是还不存在的子目录。
        Directory.CreateDirectory(directory);
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
        if (args.Contains("--print")) return Print(scenario, args).GetAwaiter().GetResult();
        return 2;
    }

    /// <summary>假 Claude Code 自带的身份版本从这个环境变量读；定位器写进 claude.cmd，改版本就是「升级」。不以 CLAUDE_ 或 ANTHROPIC_ 开头，隔离环境不会清掉它。</summary>
    public const string VersionVariable = "HOUSEHOLD_FAKE_CLAUDE_VERSION";

    /// <summary>假 Claude Code 发请求时带的身份头；<paramref name="version"/> 进 User-Agent。用例拿它核对发往官方的就是这一份。</summary>
    public static KeyValuePair<string, string>[] Identity(string version) =>
    [
        new("User-Agent", $"claude-cli/{version} (external, sdk-cli)"), new("x-app", "cli"), new("X-Stainless-OS", "FakeOS"), new("X-Stainless-Arch", "fake64"),
        new("X-Stainless-Runtime-Version", "v0.0.0-fixture"), new("anthropic-dangerous-direct-browser-access", "true")
    ];

    /// <summary>
    /// 扮演 <c>claude --print</c>：向 <c>ANTHROPIC_BASE_URL</c> 发一次带假身份头的 Messages 请求，2xx 时退出码 0、否则 1；no-identity 下一个请求都不发、退出码 3（模拟本机 Claude Code 坏了）。
    /// 身份版本取 <see cref="VersionVariable"/>，没有时取本程序文件版本的前三段。
    /// </summary>
    private static async Task<int> Print(string scenario, string[] args)
    {
        if (scenario == "no-identity") return 3;
        var version = Environment.GetEnvironmentVariable(VersionVariable) is { Length: > 0 } set ? set
            : System.Text.RegularExpressions.Regex.Match(FileVersionInfo.GetVersionInfo(Environment.ProcessPath!).ProductVersion ?? "", @"^\d+\.\d+\.\d+") is { Success: true } own ? own.Value : "0.0.0";
        var index = Array.IndexOf(args, "--model"); var model = index >= 0 && index + 1 < args.Length ? args[index + 1] : "fixture-model";
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        using var request = new HttpRequestMessage(HttpMethod.Post, Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL") + "/v1/messages?beta=true")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { model, max_tokens = 16, stream = false, system = new[] { new { type = "text", text = "fixture system" } },
                messages = new[] { new { role = "user", content = "Reply with OK." } }, tools = Array.Empty<object>() }), System.Text.Encoding.UTF8, "application/json")
        };
        foreach (var header in Identity(version)) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        request.Headers.Authorization = new("Bearer", Environment.GetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN"));
        request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01"); request.Headers.TryAddWithoutValidation("anthropic-beta", "claude-code-20250219");
        using var response = await http.SendAsync(request);
        Console.WriteLine("{}");
        return response.IsSuccessStatusCode ? 0 : 1;
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
    private string version = "1.0.0";

    /// <summary>假 Claude Code 此刻的身份版本；<see cref="Reinstall"/> 改它。</summary>
    public string Version => version;

    /// <summary>
    /// 模拟本机 Claude Code 升级或重装：换场景和身份版本，重写 claude.cmd（大小或修改时间随之改变，统一身份据此重取）；场景改成 missing 等于卸掉。
    /// </summary>
    public void Reinstall(string newScenario, string newVersion)
    {
        scenario = newScenario; version = newVersion;
        // 只删本定位器自己临时目录里的那个小脚本，下次定位时按新场景和版本重写。
        File.Delete(Path.Combine(directory, "claude.cmd"));
    }

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
            File.WriteAllText(path, $"@set {FakeClaude.VersionVariable}={version}\r\n@{command} --fake-claude {scenario} %*\r\n");
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
