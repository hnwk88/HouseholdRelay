using System.ComponentModel;
using System.Diagnostics;
using Application.Interfaces.ClaudeCode;
using Core.Configuration;
using static Tests.TestSuite;

namespace Tests;

/// <summary>自有CLI的磁盘生命周期；使用系统临时站点与无网络假进程，不接触个人Claude。</summary>
static class OwnedClaudeProcessTests
{
    /// <summary>未知starting不能自动清除；明确未启动可撤销本次登记，下一次准备不被永久阻塞。</summary>
    public static Task StartingAndFailedLaunch()
    {
        using (var home = new TestHome())
        {
            var registry = new TestProcess(home.Path).Get<IOwnedClaudeProcesses>();
            registry.Starting().Dispose();
            Refused(() => new TestProcess(home.Path).Get<IOwnedClaudeProcesses>().EnsureExited());
            Check(Directory.GetFiles(Path.Combine(home.Path, ".state", "owned-claude-processes")).Length == 1);
        }
        using (var home = new TestHome())
        {
            var registry = new TestProcess(home.Path).Get<IOwnedClaudeProcesses>();
            using var starting = registry.Starting(); var failed = false;
            try { using var unused = Process.Start(new ProcessStartInfo(Path.Combine(home.Path, "absent-fixture.exe")) { UseShellExecute = false, CreateNoWindow = true }); }
            // 只有操作系统明确拒绝创建进程的本次调用栈，才有证据撤销starting。
            catch (Win32Exception) { failed = true; starting.CancelBeforeStart(); }
            Check(failed); registry.EnsureExited();
            Check(Directory.GetFiles(Path.Combine(home.Path, ".state", "owned-claude-processes")).Length == 0);
        }
        return Task.CompletedTask;
    }

    /// <summary>真实父进程异常退出留下存活假CLI；新容器拒绝准备，假CLI真退出后自动清理并恢复。</summary>
    public static async Task ParentCrash()
    {
        using var home = new TestHome(); var registry = new TestProcess(home.Path).Get<IOwnedClaudeProcesses>();
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(OwnedClaudeProcessTests).Assembly.Location);
        start.ArgumentList.Add("--owned-cli-owner"); start.ArgumentList.Add(home.Path);
        using var owner = Process.Start(start) ?? throw new Exception("Cannot start isolated owner fixture");
        Process? child = null;
        try
        {
            var ready = await owner.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Check(ready?.StartsWith("OWNED:", StringComparison.Ordinal) == true, "Owner fixture did not register its child");
            var parts = ready!.Split(':'); child = Process.GetProcessById(int.Parse(parts[1]));
            Check(child.StartTime.ToUniversalTime().Ticks == long.Parse(parts[2]));
            // 只终止本用例刚启动的父进程，不终止子树，真实留下仍在运行的假CLI。
            owner.Kill(entireProcessTree: false); await owner.WaitForExitAsync();
            Check(!child.HasExited, "The child must survive its owner crash");
            var restarted = new TestProcess(home.Path); Refused(() => restarted.Get<IOwnedClaudeProcesses>().EnsureExited());
            await restarted.Get<ISiteIdentityService>().Prepare(default);
            Check(!restarted.Get<ISiteIdentityService>().Available, "A new service cannot prepare files while the old child remains alive");
            Check(restarted.Get<ISiteIdentityService>().Status.Contains("Claude", StringComparison.Ordinal), "Startup must preserve the safe reason that the previous CLI has not exited");
            var identity = restarted.Get<ISiteIdentityRepository>().Read(); var rejected = false;
            try { await restarted.Get<ISiteIdentityService>().Refresh(Guid.NewGuid().ToString("N"), identity.Version, default); }
            // 跨进程旧CLI也属于提交前排空，不能先换号再在文件准备阶段发现它。
            catch (ClaudeConfigurationUnavailableException) { rejected = true; }
            Check(rejected && restarted.Get<ISiteIdentityRepository>().Read() == identity, "A surviving old CLI must prevent the new identity transaction");
            // PID与启动时刻已核对，清理的仅是本用例启动的那个无网络假进程。
            child.Kill(entireProcessTree: true); await child.WaitForExitAsync();
            restarted.Get<IOwnedClaudeProcesses>().EnsureExited();
            await restarted.Get<ISiteIdentityService>().Prepare(default); Check(restarted.Get<ISiteIdentityService>().Available);
            registry.EnsureExited();
            Check(Directory.GetFiles(Path.Combine(home.Path, ".state", "owned-claude-processes")).Length == 0);
        }
        // 用例失败也回收自己创建的进程，避免测试在临时机器上留下后台任务。
        finally
        {
            if (!owner.HasExited) { owner.Kill(true); await owner.WaitForExitAsync(); }
            if (child is not null) { if (!child.HasExited) { child.Kill(true); await child.WaitForExitAsync(); } child.Dispose(); }
        }
    }

    /// <summary>测试辅助父进程：仅允许测试专属临时站点，登记一个不访问网络的长期等待进程。</summary>
    public static async Task<int> RunOwner(string[] arguments)
    {
        var home = Path.GetFullPath(arguments[1]);
        Check(Path.GetDirectoryName(home) == Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()))
            && Path.GetFileName(home).StartsWith("household-relay-foundation-", StringComparison.Ordinal));
        var registry = new TestProcess(home).Get<IOwnedClaudeProcesses>(); using var owned = registry.Starting();
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("[Threading.ManualResetEvent]::new($false).WaitOne() | Out-Null");
        using var child = Process.Start(start) ?? throw new Exception("Cannot start owned fixture child");
        owned.Register(child.Id, child.StartTime.ToUniversalTime());
        Console.WriteLine("OWNED:" + child.Id + ":" + child.StartTime.ToUniversalTime().Ticks); Console.Out.Flush();
        // 故意等待父测试杀死本进程，才能真实验证异常退出留下的磁盘登记与存活子进程。
        await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        return 0;
    }

    /// <summary>未知或活动登记只能按明确配置准备错误拒绝。</summary>
    private static void Refused(Action action)
    {
        var refused = false;
        try { action(); }
        // 不把其他进程或文件异常算成有效的失败关闭证据。
        catch (ClaudeConfigurationUnavailableException) { refused = true; }
        Check(refused, "Expected owned-process refusal");
    }
}
