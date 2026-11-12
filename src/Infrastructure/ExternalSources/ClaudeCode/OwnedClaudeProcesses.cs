using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Application.Interfaces.ClaudeCode;
using Application.Interfaces.Configuration;
using Application.Interfaces.Security;
using Core.Configuration;

namespace Infrastructure.ExternalSources.ClaudeCode;

/// <summary>受保护目录里的最小进程登记；只保存随机操作号、PID 和启动时刻，不保存账户、路径或凭据。</summary>
internal sealed class OwnedClaudeProcesses(IRelayInstance instance, ISecretProtector protector) : IOwnedClaudeProcesses
{
    private readonly string root = Path.Combine(Path.GetFullPath(instance.State), "owned-claude-processes");

    /// <summary>创建并强制落盘 starting 标记，之后才允许调用方启动子进程。</summary>
    public IOwnedClaudeProcess Starting()
    {
        CheckRoot(); protector.ProtectDirectory(root); CheckRoot();
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".json");
        WriteNew(path, new("starting", null, null));
        return new Registration(path);
    }

    /// <summary>存活或未知写入者阻止配置准备；已退出登记可幂等清除，API 服务可继续运行。</summary>
    public void EnsureExited()
    {
        CheckRoot();
        if (!Directory.Exists(root)) return;
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
        {
            if (!Path.GetExtension(path).Equals(".json", StringComparison.Ordinal) || !Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _))
                throw Unavailable();
            var marker = Read(path);
            if (marker is null) continue;
            if (marker.State != "running" || marker.ProcessId is not { } id || marker.StartedAt is not { } at || IsAlive(id, at)) throw Unavailable();
            // 已证实原进程退出或进程号已重用；只删除本站受保护登记，不终止任何进程。
            File.Delete(path);
        }
    }

    /// <summary>拒绝登记目录及其上级被重解析到工程之外，避免恢复流程跟随不可信链接。</summary>
    private void CheckRoot()
    {
        for (string? path = root; path is not null; path = Path.GetDirectoryName(path))
        {
            try { CheckFile(path); }
            // 首次创建时目录尚不存在，仍继续检查它的已有父目录。
            catch (DirectoryNotFoundException) { }
            // 尚未创建的目录在部分文件系统上报告文件不存在，同样继续检查上级。
            catch (FileNotFoundException) { }
        }
    }

    /// <summary>读取有界且结构完整的登记；丢失视为另一恢复者已清理，其他异常均失败关闭。</summary>
    private static Marker? Read(string path)
    {
        try
        {
            CheckFile(path);
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (file.Length is <= 0 or > 1024) throw Unavailable();
            var marker = JsonSerializer.Deserialize<Marker>(file) ?? throw Unavailable();
            if (marker.State is not ("starting" or "running") || marker.State == "running" && (marker.ProcessId is null or <= 0 || marker.StartedAt is null))
                throw Unavailable();
            return marker;
        }
        // 清理和进程退出登记可并发，文件已由另一方删除代表本项已完成。
        catch (FileNotFoundException) { return null; }
        // 不能把损坏登记或权限错误当作没有进程，也不能把原始目录带进页面。
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException) { throw Unavailable(); }
    }

    /// <summary>比较 PID 与启动时刻；进程已退出或同号新进程均表示原写入者不在，查询失败则保持关闭。</summary>
    private static bool IsAlive(int processId, DateTimeOffset startedAt)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == startedAt.UtcTicks;
        }
        // 系统已经没有此 PID，原进程确实退出。
        catch (ArgumentException) { return false; }
        // 系统拒绝检查进程时不得猜测其已退出。
        catch (Exception error) when (error is Win32Exception or InvalidOperationException) { throw Unavailable(); }
    }

    /// <summary>新文件先独占创建并强制落盘，崩溃留下空文件也会在恢复时失败关闭。</summary>
    private static void WriteNew(string path, Marker marker)
    {
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        JsonSerializer.Serialize(output, marker); output.Flush(true);
    }

    /// <summary>读取或替换前拒绝重解析点，不跟随链接访问其他数据。</summary>
    private static void CheckFile(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw Unavailable();
    }

    /// <summary>固定的恢复说明不暴露 PID、目录、账户或配置原文。</summary>
    private static ClaudeConfigurationUnavailableException Unavailable() => new("本站上次启动的 Claude 程序尚未完成收尾，官方账户保持暂停。请先确认本站旧程序已经结束，再继续设备准备；若仍不能恢复，请由维护人员处理。独立 API 仍可使用。");

    /// <summary>唯一持久内容；随机操作号只存在于受保护文件名。</summary>
    private sealed record Marker(string State, int? ProcessId, DateTimeOffset? StartedAt);

    /// <summary>登记句柄本身不持有阻塞锁，子进程异常或父服务崩溃都保留磁盘证据。</summary>
    private sealed class Registration(string path) : IOwnedClaudeProcess
    {
        private bool registered;
        private bool completed;

        /// <summary>写临时文件后原子替换 starting，保持原登记的权限。</summary>
        public void Register(int processId, DateTimeOffset startedAt)
        {
            if (registered || completed || processId <= 0 || startedAt == default) throw Unavailable();
            var temporary = path + ".tmp";
            try
            {
                WriteNew(temporary, new("running", processId, startedAt.ToUniversalTime()));
                CheckFile(path); File.Replace(temporary, path, null); registered = true;
            }
            // 只移除本次已知临时文件，主 starting 标记无论失败原因都保留供重启检查。
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        /// <summary>只有登记过且已退出的进程可完成，未知 starting 不会被 Dispose 或错误清理抹去。</summary>
        public void Complete()
        {
            if (completed) return;
            if (!registered) throw Unavailable();
            var marker = Read(path);
            if (marker is not null && (marker.State != "running" || marker.ProcessId is not { } id || marker.StartedAt is not { } at || IsAlive(id, at)))
                throw Unavailable();
            // 已确认生命周期结束，删除本次随机登记；重复完成或恢复清理均安全。
            File.Delete(path); completed = true;
        }

        /// <summary>调用方必须掌握系统启动失败的明确结果；有 PID 或已完成的记录不能按未启动处理。</summary>
        public void CancelBeforeStart()
        {
            if (registered || completed) throw Unavailable();
            var marker = Read(path);
            if (marker is not { State: "starting", ProcessId: null, StartedAt: null }) throw Unavailable();
            // 当前调用栈见证操作系统拒绝启动，只删除尚未启动的本次登记，不用于重启后猜测。
            File.Delete(path); completed = true;
        }

        /// <summary>不自动删除登记：没有显式完成就没有足够证据断定子进程已退出。</summary>
        public void Dispose() { }
    }
}
