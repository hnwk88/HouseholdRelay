using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Application.Interfaces.ClaudeCode;
using Application.Interfaces.Configuration;
using Application.Interfaces.Upstream;
using Core.Upstream;

namespace Infrastructure.ExternalSources.ClaudeCode;

/// <summary>
/// 发往官方账户的统一身份，按入口各一份：某个入口第一次要用时让本机 claude.exe 以那种模式向本机临时端点发一次请求，记下它带的身份头；之后每次要用先核对 claude.exe 的路径、大小和修改时间，
/// 三样有一样变了（升级、重装、换了位置）就自动重取。并发请求同时撞上重取时只起一个子进程，其余的等它的结果；重取失败记 60 秒，期间直接读出同一句原因，不反复起子进程。
/// </summary>
internal sealed class ClaudeCodeIdentitySource(IClientLauncher launcher, IClaudeCodeProbe probe, TimeProvider clock,
    ISiteIdentityService siteIdentity) : IClaudeCodeIdentitySource
{
    /// <summary>重取失败后这么久之内不再重取，直接读出上次的原因。</summary>
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(60);
    /// <summary>一次重取最多等这么久（本机启动 Claude Code 通常几秒）。</summary>
    private static readonly TimeSpan CaptureLimit = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);

    /// <summary>
    /// 返回入口 <paramref name="entrypoint"/> 与本机此刻这份 claude.exe 对得上的身份：指纹没变直接用已取到的；变了或从没取过就在本机以那种模式重取一次（不碰官方），失败时抛出读给人听的原因。
    /// 不能实抓的入口按 <see cref="ClaudeCodeClient.DefaultEntrypoint"/> 取。
    /// </summary>
    public async Task<ClaudeCodeIdentity> Current(string entrypoint, CancellationToken cancellation)
    {
        using var operation = siteIdentity.Enter();
        if (!ClaudeCodeClient.CapturableEntrypoints.Contains(entrypoint)) entrypoint = ClaudeCodeClient.DefaultEntrypoint;
        var (executable, fingerprint) = Locate();
        if (Known(entrypoint, fingerprint) is { } known) return known;
        await gate.WaitAsync(cancellation);
        try
        {
            // 排在前面的那个请求可能刚取完，同一份 claude.exe 的同一入口不再取第二次。
            if (Known(entrypoint, fingerprint) is { } settled) return settled;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(CaptureLimit);
            ClaudeCodeIdentity identity;
            try { identity = ClaudeCodeIdentity.From(await probe.Capture(entrypoint, timeout.Token), FileVersion(executable), entrypoint, clock.GetUtcNow()); }
            // 取不全、对不上：记下这句原因。
            catch (ClaudeCodeIdentityUnavailableException error) { throw Remember(entrypoint, fingerprint, error.Message); }
            // 代理已启用却取不到地址：原样交给调用方按代理不可用处理，不记成身份问题。
            catch (UpstreamProxyUnavailableException) { throw; }
            // 本机 Claude Code 没发请求就退出、或会话没走完。
            catch (InvalidOperationException) { throw Remember(entrypoint, fingerprint, ClaudeCodeIdentity.CaptureFailed); }
            // 系统起不来 claude.exe（文件损坏或被拦截），或建不了伪控制台。
            catch (Win32Exception) { throw Remember(entrypoint, fingerprint, ClaudeCodeIdentity.CaptureFailed); }
            // 本机临时端点读写失败。
            catch (IOException) { throw Remember(entrypoint, fingerprint, ClaudeCodeIdentity.CaptureFailed); }
            // 调用方没取消却到了时限：本机 Claude Code 卡住了。
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw Remember(entrypoint, fingerprint, ClaudeCodeIdentity.CaptureFailed); }
            entries[entrypoint] = new(fingerprint, identity, null, clock.GetUtcNow());
            return identity;
        }
        // 成败都放开闸门，排队等着的请求才能读到结果或接着重取，不会一直卡住。
        finally { gate.Release(); }
    }

    /// <summary>
    /// 此刻身份的状态读成一段话：各入口已取到的版本与取得时刻、上次重取失败的原因、找不到 Claude Code，或还没取过；只核对指纹，不启动 Claude Code。
    /// 例如「发往官方账户的请求按客户端的入口，用中转所在电脑 Claude Code 的身份：终端交互 cli 用 2.1.316（2026-11-01 15:02 取得）。本机 Claude Code 升级后自动重取。」
    /// </summary>
    public string Describe()
    {
        try
        {
            var (_, fingerprint) = Locate();
            var current = entries.Where(item => item.Value.Fingerprint == fingerprint).OrderBy(item => item.Key, StringComparer.Ordinal).ToArray();
            var taken = current.Where(item => item.Value.Identity is not null)
                .Select(item => $"{EntrypointLabel(item.Key)} 用 {item.Value.Identity!.Version}（{TimeZoneInfo.ConvertTime(item.Value.Identity.CapturedAt, clock.LocalTimeZone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} 取得）").ToArray();
            var failures = current.Select(item => item.Value.Failure).OfType<string>().Distinct().ToArray();
            var head = taken.Length > 0 ? $"发往官方账户的请求按客户端的入口，用中转所在电脑 Claude Code 的身份：{string.Join("；", taken)}。本机 Claude Code 升级后自动重取。"
                : "发往官方账户的请求按客户端的入口，用中转所在电脑 Claude Code 的身份，每种入口第一次发往官方时自动从本机取一次。";
            return string.Join("", failures.Prepend(head));
        }
        // 本机找不到 Claude Code：读出原因与下一步。
        catch (ClaudeCodeIdentityUnavailableException error) { return error.Message; }
    }

    /// <summary>入口的读法：cli 读「终端交互 cli」，sdk-cli 读「SDK 与 --print 的 sdk-cli」，claude-vscode 读「VS Code 的 claude-vscode」，其余原样。</summary>
    internal static string EntrypointLabel(string entrypoint) => entrypoint switch
    { "cli" => "终端交互 cli", "sdk-cli" => "SDK 与 --print 的 sdk-cli", "claude-vscode" => "VS Code 的 claude-vscode", _ => entrypoint };

    /// <summary>同一份 claude.exe 这个入口已取到的身份；上次重取失败且还在 60 秒内时直接抛出那句原因；其余情况为 null，要重取。</summary>
    private ClaudeCodeIdentity? Known(string entrypoint, string fingerprint)
    {
        if (!entries.TryGetValue(entrypoint, out var known) || known.Fingerprint != fingerprint) return null;
        if (known.Identity is not null) return known.Identity;
        if (clock.GetUtcNow() - known.At < RetryAfter) throw new ClaudeCodeIdentityUnavailableException(known.Failure!);
        return null;
    }

    /// <summary>记下这份 claude.exe 这个入口的重取失败与时刻，返回要抛出的异常。</summary>
    private ClaudeCodeIdentityUnavailableException Remember(string entrypoint, string fingerprint, string failure)
    {
        entries[entrypoint] = new(fingerprint, null, failure, clock.GetUtcNow());
        return new(failure);
    }

    /// <summary>找本机 claude.exe，并以完整路径、字节数和修改时间作为它的指纹；找不到或刚被删掉时抛「找不到」。</summary>
    private (string Executable, string Fingerprint) Locate()
    {
        try
        {
            var executable = launcher.FindClaude();
            var file = new FileInfo(executable);
            if (!file.Exists) throw new ClaudeCodeIdentityUnavailableException(ClaudeCodeIdentity.NotFound);
            return (executable, string.Join('|', file.FullName, file.Length.ToString(CultureInfo.InvariantCulture), file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture), siteIdentity.Current.Version.ToString(CultureInfo.InvariantCulture)));
        }
        // 定位器报告没装。
        catch (InvalidOperationException error) when (error is not ClaudeCodeIdentityUnavailableException) { throw new ClaudeCodeIdentityUnavailableException(ClaudeCodeIdentity.NotFound); }
    }

    /// <summary>claude.exe 的文件版本（如 2.1.312.0）；文件没有版本资源（例如脚本）时为 null。</summary>
    private static string? FileVersion(string executable) => FileVersionInfo.GetVersionInfo(executable).ProductVersion is { Length: > 0 } version ? version : null;

    /// <summary>一份 claude.exe 的取身份结果：取到了是 <paramref name="Identity"/>，失败了是 <paramref name="Failure"/> 与失败时刻。</summary>
    private sealed record Entry(string Fingerprint, ClaudeCodeIdentity? Identity, string? Failure, DateTimeOffset At);
}
