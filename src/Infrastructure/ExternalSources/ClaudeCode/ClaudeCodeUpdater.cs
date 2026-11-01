using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Application.Interfaces.ClaudeCode;
using Application.Interfaces.Upstream;
using Core.Upstream;

namespace Infrastructure.ExternalSources.ClaudeCode;

/// <summary>
/// 让中转所在电脑的 Claude Code 跟上家里的客户端：发往官方账户的请求里看到更新的客户端版本时，后台跑一次本机安装方式自带的 <c>claude update</c>（npm 装的走 npm，原生装的走原生更新器，由它自己判断）。
/// 同一时刻只跑一次，升级在跑时不再起；失败后 <see cref="RetryAfter"/> 之内不再自动试，管理员按钮不受这个间隔限制。升没升上去不看退出码（已是最新、锁被占用也退出 0），
/// 看升级前后两次 <c>claude --version</c>。子进程用隔离环境和全局代理，配置目录是用完即删的临时目录，不碰官方账户目录和刷新令牌。
/// </summary>
internal sealed partial class ClaudeCodeUpdater(IClientLauncher launcher, IUpstreamProxy proxy, TimeProvider clock) : IClaudeCodeUpdater
{
    /// <summary>自动升级失败后这么久之内不再自动试。</summary>
    internal static readonly TimeSpan RetryAfter = TimeSpan.FromHours(1);
    /// <summary>一次升级最多等这么久（要下载安装包）。</summary>
    private static readonly TimeSpan UpdateLimit = TimeSpan.FromMinutes(10);
    /// <summary>升级命令原话进读句的最大字符数（含截断时补的省略号）。</summary>
    private const int QuoteLimit = 200;
    private readonly Lock sync = new();
    private Task<string>? running;
    private Seen? seen;
    private Outcome? last;

    /// <summary>记下客户端与中转的版本；客户端更新、没有升级在跑、离上次失败已过 <see cref="RetryAfter"/> 时在后台起一次升级，立即返回。</summary>
    public void Observe(string? clientVersion, string relayVersion, string clientId)
    {
        // 判断「要不要起」与「记下正在跑」在同一把锁里，并发的请求只会有一个起升级。
        lock (sync)
        {
            seen = new(clientVersion, clientId, relayVersion, clock.GetUtcNow());
            if (ClaudeCodeClient.Compare(clientVersion, relayVersion) is not > 0 || running is not null) return;
            if (last is { Succeeded: false } failed && clock.GetUtcNow() - failed.At < RetryAfter) return;
            running = Begin();
        }
    }

    /// <summary>管理员按钮：已在升级时等那一次，否则立即起一次（不看失败间隔），返回结果句。</summary>
    public Task<string> Update(CancellationToken cancellation)
    {
        Task<string> task;
        // 与自动升级共用同一个「正在跑」：按钮撞上后台升级时等那一次，不另起。
        lock (sync) task = running ??= Begin();
        return task.WaitAsync(cancellation);
    }

    /// <summary>中转版本、最近一次看到的客户端版本（使用者按主键读显示名）、两边谁新、上次升级的时间与结果，读成一段话；不启动 Claude Code。</summary>
    public string Describe(Func<string, string> displayName)
    {
        Seen? client; Outcome? outcome; bool busy;
        // 三样在同一把锁里一起读，读句里的版本、结果与「正在升级」对得上。
        lock (sync) { client = seen; outcome = last; busy = running is not null; }
        var text = new StringBuilder();
        if (client is null) text.Append("还没有发往官方账户的请求，中转与客户端的 Claude Code 版本要等第一次发往官方后才读得出。");
        else
        {
            text.Append($"中转所在电脑的 Claude Code 是 {client.RelayVersion}。");
            if (client.ClientVersion is null) text.Append($"最近一次发往官方账户的是「{displayName(client.ClientId)}」，{Time(client.At)}，看不出它的 Claude Code 版本。");
            else
            {
                text.Append($"最近一次看到的客户端是「{displayName(client.ClientId)}」的 Claude Code {client.ClientVersion}，{Time(client.At)}。");
                text.Append(ClaudeCodeClient.Compare(client.ClientVersion, client.RelayVersion) switch
                {
                    > 0 when busy => "它比中转的新，正在后台升级中转的 Claude Code，升级时请求照常发。",
                    > 0 when outcome is { Succeeded: false } => $"它比中转的新，上次升级没有成功，{Time(outcome.At + RetryAfter)}以后再自动试，也可以现在按「升级中转的 Claude Code」。",
                    > 0 => "它比中转的新，下一个发往官方账户的请求会在后台升级中转的 Claude Code。",
                    < 0 => "这台客户端的 Claude Code 比中转的旧，请在那台电脑上升级。",
                    _ => "两边版本相同。"
                });
            }
        }
        text.Append(outcome is null ? "还没有升级过中转的 Claude Code。" : $"上次升级：{Time(outcome.At)}，{outcome.Sentence}");
        return text.ToString();
    }

    /// <summary>在线程池上跑一次升级，结束时记下结果并放开「只跑一次」；结果句也作为任务的值交给等它的按钮。</summary>
    private Task<string> Begin() => Task.Run(async () =>
    {
        Outcome outcome;
        try { outcome = await Upgrade(); }
        // 升级器自己的意外：读成失败，不让后台任务的异常无人接住。
        catch (Exception error) when (error is IOException or Win32Exception or InvalidOperationException or OperationCanceledException)
        { outcome = new(clock.GetUtcNow(), false, "升级没能完成：" + Clip(error.Message)); }
        lock (sync) { last = outcome; running = null; }
        return outcome.Sentence;
    });

    /// <summary>
    /// 升一次：先读当前版本，再以隔离环境跑 <c>claude update</c>（限时 10 分钟，输出最多留 64 KB），再读一次版本。版本变新算成功；
    /// 版本没变（已是能取到的最新、锁被占用、包管理器没动）或退出码非 0 算没升上去，读句带升级命令原话。
    /// </summary>
    private async Task<Outcome> Upgrade()
    {
        string executable;
        try { executable = launcher.FindClaude(); }
        // 本机没装：没有可升级的。
        catch (InvalidOperationException) { return new(clock.GetUtcNow(), false, "中转所在电脑找不到 Claude Code，没有升级。请在中转所在电脑重新安装 Claude Code。"); }
        var before = await Version();
        var directory = Path.Combine(Path.GetTempPath(), "HouseholdRelay-update-" + Guid.NewGuid().ToString("N"));
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        IsolatedClaudeEnvironment.Apply(start, directory, proxy.Resolve(OfficialAccountLogin.OfficialTarget));
        start.ArgumentList.Add("update");
        int exitCode; string said;
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 claude update。");
            process.PriorityClass = ProcessPriorityClass.BelowNormal; process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(UpdateLimit);
            var output = Collect(process.StandardOutput, timeout.Token); var errors = Collect(process.StandardError, timeout.Token);
            try { await process.WaitForExitAsync(timeout.Token); }
            // 到了时限还没完：结束整棵进程树，按没升上去读出。
            catch (OperationCanceledException) { process.Kill(true); await process.WaitForExitAsync(); return new(clock.GetUtcNow(), false, $"升级超过 {UpdateLimit.TotalMinutes} 分钟没有完成，已停下，版本仍是 {before ?? "未知"}。"); }
            said = (await errors + " " + await output).Trim(); exitCode = process.ExitCode;
        }
        finally
        {
            // 只删本次自己在系统临时目录下建的配置目录；被占用或没权限时放弃，残留的只是临时配置。
            try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            // 文件仍被占用：放弃清理。
            catch (IOException) { }
            // 没有删除权限：放弃清理。
            catch (UnauthorizedAccessException) { }
        }
        var after = await Version();
        var quote = Clip(said) is { Length: > 0 } text ? $"升级命令原话：{text}" : "";
        if (ClaudeCodeClient.Compare(after, before) > 0) return new(clock.GetUtcNow(), true, $"已把中转的 Claude Code 从 {before} 升到 {after}，下一个发往官方账户的请求自动换用新身份。");
        return new(clock.GetUtcNow(), false, (exitCode == 0 ? $"没有升上去，版本仍是 {after ?? before ?? "未知"}。" : $"升级失败（退出码 {exitCode}），版本仍是 {after ?? before ?? "未知"}。") + quote);
    }

    /// <summary>用现成的 <see cref="IClientLauncher.DetectUserAgent"/> 读本机 Claude Code 版本；读不出时为 null。</summary>
    private async Task<string?> Version()
    {
        try { return VersionPattern().Match(await launcher.DetectUserAgent()) is { Success: true } found ? found.Value : null; }
        // 起不来、超时或认不出版本：读成未知。
        catch (Exception error) when (error is InvalidOperationException or Win32Exception or OperationCanceledException) { return null; }
    }

    /// <summary>读完一路输出，最多留前 64 KB；到时限时返回已读到的部分。</summary>
    private static async Task<string> Collect(StreamReader reader, CancellationToken cancellation)
    {
        var text = new StringBuilder(); var buffer = new char[4096];
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer, cancellation)) > 0) if (text.Length < 65536) text.Append(buffer, 0, Math.Min(count, 65536 - text.Length));
        }
        // 到时限或进程被结束：交出已读到的部分。
        catch (OperationCanceledException) { }
        // 管道断开：交出已读到的部分。
        catch (IOException) { }
        return text.ToString();
    }

    /// <summary>去掉地址里的用户名与密码（例如代理凭据），控制字符与连续空白换成一个空格，截到 <see cref="QuoteLimit"/> 字以内；先去秘密后截断。</summary>
    internal static string Clip(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var text = Spaces().Replace(Credentials().Replace(value, "://[redacted]@"), " ").Trim();
        if (text.Length <= QuoteLimit) return text;
        var cut = QuoteLimit - 1;
        if (char.IsHighSurrogate(text[cut - 1])) cut--;
        return text[..cut] + "…";
    }

    /// <summary>按本机时区读成「yyyy-MM-dd HH:mm」。</summary>
    private string Time(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, clock.LocalTimeZone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>「主.次.修订」三段数字。</summary>
    [GeneratedRegex(@"\d+\.\d+\.\d+", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    /// <summary>地址里的「用户名:密码@」。</summary>
    [GeneratedRegex(@"://[^/\s@]+@", RegexOptions.CultureInvariant)]
    private static partial Regex Credentials();

    /// <summary>控制字符与连续空白。</summary>
    [GeneratedRegex(@"[\s\p{Cc}]+", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();

    /// <summary>最近一次发往官方账户的请求：客户端版本（看不出时为 null）、使用者主键、中转版本与时刻。</summary>
    private sealed record Seen(string? ClientVersion, string ClientId, string RelayVersion, DateTimeOffset At);

    /// <summary>一次升级的结果：时刻、是否升上去、读句。</summary>
    private sealed record Outcome(DateTimeOffset At, bool Succeeded, string Sentence);
}
