using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Application.Interfaces.ClaudeCode;
using Application.Interfaces.Configuration;
using Application.Interfaces.Security;
using Application.Interfaces.Upstream;
using Core.Upstream;

namespace Infrastructure.ExternalSources.ClaudeCode;

/// <summary>
/// 照 MyCoAI <c>AuthCliRunner</c> 委托本机 Claude Code 登录官方账户：每个账户一个独立配置目录 <c>.state\official-accounts\上游主键</c>，
/// 子进程环境由 <see cref="IsolatedClaudeEnvironment"/> 隔离、代理按全局设置；令牌刷新交给 CLI，中转只读最新的凭据文件。
/// </summary>
internal sealed class OfficialAccountLogin(IClientLauncher launcher, IUpstreamProxy proxy, ISecretProtector protector, IRelayInstance instance,
    TimeProvider clock, OfficialLoginOptions options) : IOfficialAccountLogin
{
    /// <summary>令牌离到期不足这么久就让 CLI 刷新。</summary>
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);
    /// <summary>令牌已过期时，转发最多等刷新这么久。</summary>
    private static readonly TimeSpan RefreshWait = TimeSpan.FromSeconds(30);
    /// <summary>同一账户两次刷新之间至少隔这么久，刷新失败时不至于每个请求都起一个 CLI。</summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1);
    /// <summary>登录与刷新按官方接口地址解析代理。</summary>
    private static readonly Uri OfficialTarget = new("https://" + UpstreamDetection.OfficialHost);
    private readonly Dictionary<string, (Task Task, DateTimeOffset StartedAt)> refreshes = new(StringComparer.Ordinal);

    /// <summary>
    /// 定位 claude.exe、按全局设置解析代理（启用却取不到时抛出 <see cref="UpstreamProxyUnavailableException"/>），建好受保护的配置目录后运行 <c>claude auth login</c> 等它结束；
    /// 成功且目录里有凭据时用 <c>auth status --json</c> 取邮箱。任何失败都删掉这个目录再抛出读给管理员的原因。
    /// </summary>
    public async Task<string?> Login(string accountId, CancellationToken cancellation)
    {
        string executable;
        try { executable = launcher.FindClaude(); }
        // 定位失败只说明本机没装，换成登录场景的原因与下一步。
        catch (InvalidOperationException) { throw new InvalidOperationException("本机没有找到 Claude Code，无法登录官方账户。请先安装 Claude Code，再按「登录官方账户」。"); }
        var route = proxy.Resolve(OfficialTarget);
        var directory = Directory(accountId);
        protector.ProtectDirectory(directory);
        try
        {
            var start = Start(executable, directory, route, "auth", "login");
            if (options.ShowWindow) { start.CreateNoWindow = false; start.WindowStyle = ProcessWindowStyle.Minimized; }
            else start.CreateNoWindow = true;
            // 调用方取消（浏览器断开）与登录限时合成一个令牌，任一触发都结束登录进程；两者靠调用方令牌是否已取消来区分读法。
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation); limit.CancelAfter(options.LoginTimeout);
            int exit;
            using (var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 Claude Code 登录，请再按一次「登录官方账户」。"))
            {
                try { await process.WaitForExitAsync(limit.Token); exit = process.ExitCode; }
                // 只有限时到了才换成超时说明；浏览器断开等调用方取消原样抛出。
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                { throw new InvalidOperationException($"等了 {options.LoginTimeout.TotalMinutes:0} 分钟还没有登录完成，已停止。需要时再按「登录官方账户」重试。"); }
                // 超时、取消或等待出错时结束登录进程树，不留后台进程。
                finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); } }
            }
            if (exit != 0) throw new InvalidOperationException($"登录没有完成（Claude Code 登录以退出码 {exit} 结束），可能是在浏览器里取消了。需要时再按「登录官方账户」重试。");
            if (Read(directory) is null)
                throw new InvalidOperationException("登录命令结束了，但没有拿到官方账户凭据。请再按一次「登录官方账户」；仍然不行时可以改用下面的「贴账户访问令牌」。");
            return await Email(executable, directory, route, cancellation);
        }
        // 登录没成功就不留半截目录；异常原样交给调用方读出。
        catch { Remove(directory); throw; }
    }

    /// <summary>读最新凭据；离到期不足 <see cref="RefreshMargin"/> 时在后台让 CLI 刷新，已过期时最多等 <see cref="RefreshWait"/> 后重读，刷新不成仍返回旧令牌，由上游的拒绝触发重新登录提示。</summary>
    public string? ReadAccessToken(string accountId)
    {
        var directory = Directory(accountId);
        var credential = Read(directory);
        if (credential?.ExpiresAt is not { } expires || expires - clock.GetUtcNow() > RefreshMargin) return credential?.AccessToken;
        var refresh = Refresh(accountId, directory);
        if (expires > clock.GetUtcNow()) return credential.AccessToken;
        refresh.Wait(RefreshWait);
        return Read(directory)?.AccessToken ?? credential.AccessToken;
    }

    /// <summary>同一账户同时只跑一次刷新，且两次之间至少隔 <see cref="RefreshInterval"/>；否则返回上一次的任务。</summary>
    private Task Refresh(string accountId, string directory)
    {
        // 并发请求同时发现令牌快到期：查上一次刷新与登记新刷新必须在同一把锁里，否则会各起一个 CLI 同时改写凭据文件。
        lock (refreshes)
        {
            if (refreshes.TryGetValue(accountId, out var last) && (!last.Task.IsCompleted || clock.GetUtcNow() - last.StartedAt < RefreshInterval)) return last.Task;
            var task = Task.Run(() => RunRefresh(directory));
            refreshes[accountId] = (task, clock.GetUtcNow());
            return task;
        }
    }

    /// <summary>
    /// 在账户目录里跑一次 <c>claude -p /usage</c>（MyCoAI 用的本地免费命令，不花模型额度）：CLI 发现令牌快到期会自己用刷新令牌换新并写回凭据文件。
    /// 失败只说明这次没刷新成，不抛出，转发继续用旧令牌。
    /// </summary>
    private async Task RunRefresh(string directory)
    {
        try
        {
            var start = Start(launcher.FindClaude(), directory, proxy.Resolve(OfficialTarget), "-p", "/usage", "--output-format", "json", "--no-session-persistence");
            await RunHidden(start, TimeSpan.FromSeconds(60), CancellationToken.None);
        }
        // 没装 Claude Code、代理取不到、进程起不来或超时：这次不刷新，令牌真失效时上游拒绝会提示重新登录。
        catch (Exception error) when (error is InvalidOperationException or Win32Exception or IOException or OperationCanceledException) { }
    }

    /// <summary>跑 <c>auth status --json</c> 取 <c>email</c>；命令失败、超时或输出不是预期 JSON 时返回 null，不影响登录结果。</summary>
    private static async Task<string?> Email(string executable, string directory, ProxyRoute route, CancellationToken cancellation)
    {
        try
        {
            var (exit, output) = await RunHidden(Start(executable, directory, route, "auth", "status", "--json"), TimeSpan.FromSeconds(30), cancellation);
            if (exit != 0) return null;
            using var document = JsonDocument.Parse(output);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("email", out var email)
                && email.ValueKind == JsonValueKind.String && email.GetString() is { Length: > 0 } value && !value.Any(char.IsControl) ? value.Trim() : null;
        }
        // 只是取不到邮箱：名称退回默认值，登录本身已经成功。
        catch (Exception error) when (error is JsonException or InvalidOperationException or Win32Exception or IOException
            || error is OperationCanceledException && !cancellation.IsCancellationRequested) { return null; }
    }

    /// <summary>组装账户目录里的 Claude Code 子进程：工作目录与配置目录都是账户目录，环境按 <see cref="IsolatedClaudeEnvironment"/> 隔离。</summary>
    internal static ProcessStartInfo Start(string executable, string directory, ProxyRoute route, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = directory, UseShellExecute = false };
        IsolatedClaudeEnvironment.Apply(start, directory, route);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    /// <summary>不建窗口运行子进程并读完标准输出，限时 <paramref name="limit"/>；超时或取消时结束进程树并抛出 <see cref="OperationCanceledException"/>。</summary>
    private static async Task<(int Exit, string Output)> RunHidden(ProcessStartInfo start, TimeSpan limit, CancellationToken cancellation)
    {
        start.CreateNoWindow = true; start.RedirectStandardInput = true; start.RedirectStandardOutput = true; start.RedirectStandardError = true;
        start.StandardOutputEncoding = new UTF8Encoding(false); start.StandardErrorEncoding = new UTF8Encoding(false);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 Claude Code。");
        // 调用方取消与本次限时合成一个令牌，读输出和等退出都受它约束，任一触发都在 finally 里结束进程树。
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(limit);
        try
        {
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token); var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token); await errors;
            return (process.ExitCode, await output);
        }
        // 超时、取消或读写出错时结束进程树，不留后台进程。
        finally { if (!process.HasExited) process.Kill(true); }
    }

    /// <summary>读并解析凭据文件；CLI 正在改写时可能读到半截或被占用，稍等重读，三次仍不行或文件不存在时返回 null。</summary>
    private static OfficialCredential? Read(string directory)
    {
        var path = Path.Combine(directory, OfficialCredential.FileName);
        for (var attempt = 1; ; attempt++)
        {
            try { return File.Exists(path) ? OfficialCredential.Parse(File.ReadAllBytes(path)) : null; }
            // 半截正文或文件被占用：最多重读三次，之后按没有凭据处理。
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
            { if (attempt == 3) return null; Thread.Sleep(50); }
        }
    }

    /// <summary>账户目录的完整路径；主键须是 32 位小写十六进制（上游主键的格式），否则拒绝，防止拼出数据目录以外的路径。</summary>
    private string Directory(string accountId)
    {
        if (accountId.Length != 32 || !accountId.All(char.IsAsciiHexDigitLower)) throw new InvalidOperationException("上游主键无效，请重新加载页面。");
        return Path.Combine(instance.State, "official-accounts", accountId);
    }

    /// <summary>删除登录失败留下的账户目录；删不掉时放弃，残留的只是一个没有凭据或未被任何上游引用的目录。</summary>
    private static void Remove(string directory)
    {
        try { if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, true); }
        // 文件被占用：放弃清理，不掩盖登录失败的真实原因。
        catch (IOException) { }
        // 没有删除权限：同样放弃清理。
        catch (UnauthorizedAccessException) { }
    }
}
