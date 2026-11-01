using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace Infrastructure.ExternalSources.ClaudeCode;

/// <summary>
/// 在 Windows 伪控制台（ConPTY）里起一个控制台程序：它看到的是真终端，所以 Claude Code 按交互模式运行；不建任何窗口，屏幕输出在后台读掉。
/// 屏幕上出现 <c>answers</c> 里的提示时按一次对应的键（例如首次运行的「Press Enter to continue」），每种只按一次。用完释放时结束整棵进程树并关掉伪控制台。
/// </summary>
internal sealed partial class PseudoConsoleProcess : IDisposable
{
    private const uint ExtendedStartupInfoPresent = 0x00080000, CreateUnicodeEnvironment = 0x00000400;
    private const int UseStandardHandles = 0x00000100;
    private static readonly IntPtr PseudoConsoleAttribute = 0x00020016;
    private readonly IntPtr console, attributes, environment, processHandle, threadHandle;
    private readonly FileStream input, output;
    private readonly StringBuilder screen = new();
    private int disposed;

    /// <summary>伪控制台里那个进程；结束、等待退出与读退出码都用它。</summary>
    public Process Process { get; }

    /// <summary>屏幕输出读到头（进程退出、伪控制台关闭）时完成。</summary>
    public Task Drained { get; }

    /// <summary>
    /// 按 <paramref name="start"/> 的程序、参数、环境与工作目录在新伪控制台里起进程（<c>.cmd</c> 与 <c>.bat</c> 经 cmd.exe 跑），不用它的重定向设置；
    /// <paramref name="answers"/> 是「屏幕提示 → 要按的键」。系统调用失败时抛 <see cref="Win32Exception"/>，已建的句柄都会释放。
    /// </summary>
    private PseudoConsoleProcess(ProcessStartInfo start, IReadOnlyDictionary<string, string> answers)
    {
        SafeFileHandle? inputRead = null, inputWrite = null, outputRead = null, outputWrite = null;
        try
        {
            if (!CreatePipe(out inputRead, out inputWrite, IntPtr.Zero, 0) || !CreatePipe(out outputRead, out outputWrite, IntPtr.Zero, 0)) throw new Win32Exception();
            var result = CreatePseudoConsole(new Size { X = 120, Y = 40 }, inputRead, outputWrite, 0, out console);
            if (result != 0) throw new Win32Exception(result);
            var size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)
                || !UpdateProcThreadAttribute(attributes, 0, PseudoConsoleAttribute, console, IntPtr.Size, IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception();
            // 标准句柄留空并声明使用：否则子进程会继承父进程被重定向的管道，看不到终端。
            var startup = new StartupInfoEx { StartupInfo = new StartupInformation { Size = Marshal.SizeOf<StartupInfoEx>(), Flags = UseStandardHandles }, AttributeList = attributes };
            environment = Marshal.StringToHGlobalUni(string.Concat(start.Environment.Where(item => item.Value is not null)
                .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase).Select(item => item.Key + "=" + item.Value + "\0")) + "\0");
            var directory = string.IsNullOrEmpty(start.WorkingDirectory) ? Environment.CurrentDirectory : start.WorkingDirectory;
            if (!CreateProcess(null, new StringBuilder(CommandLine(start)), IntPtr.Zero, IntPtr.Zero, false, ExtendedStartupInfoPresent | CreateUnicodeEnvironment,
                environment, directory, ref startup, out var information)) throw new Win32Exception();
            processHandle = information.Process; threadHandle = information.Thread;
            Process = Process.GetProcessById(information.ProcessId);
            // 伪控制台已持有自己那一端，本进程的这两端关掉，进程退出后读端才会读到头。
            inputRead.Dispose(); outputWrite.Dispose();
            input = new FileStream(inputWrite, FileAccess.Write, 1); output = new FileStream(outputRead, FileAccess.Read, 4096);
            Drained = Task.Run(() => Read(answers));
        }
        // 建到一半失败：关掉已建的管道、伪控制台与内存，再把原因交给调用方。
        catch
        {
            inputRead?.Dispose(); inputWrite?.Dispose(); outputRead?.Dispose(); outputWrite?.Dispose();
            Release();
            throw;
        }
    }

    /// <summary>在新伪控制台里起 <paramref name="start"/> 描述的进程；屏幕出现 <paramref name="answers"/> 的键（提示文字）时按对应的值一次。</summary>
    public static PseudoConsoleProcess Start(ProcessStartInfo start, IReadOnlyDictionary<string, string> answers) => new(start, answers);

    /// <summary>结束整棵进程树、关掉伪控制台与管道，释放句柄与内存；可重复调用。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { if (!Process.HasExited) Process.Kill(true); }
        // 进程恰好在这一刻自己退出了：没有要结束的。
        catch (InvalidOperationException) { }
        // 进程树里有的进程没有权限结束：已尽力，其余由关掉伪控制台带走。
        catch (Win32Exception) { }
        Release();
        input.Dispose(); output.Dispose(); Process.Dispose();
    }

    /// <summary>后台读屏幕：去掉终端控制序列后找提示，每种提示按一次键；只留最后 16 KB 屏幕文字，进程退出后读到头即结束。</summary>
    private async Task Read(IReadOnlyDictionary<string, string> answers)
    {
        var buffer = new byte[4096]; var pressed = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            int count;
            while ((count = await output.ReadAsync(buffer)) > 0)
            {
                screen.Append(Encoding.UTF8.GetString(buffer, 0, count));
                if (screen.Length > 16384) screen.Remove(0, screen.Length - 16384);
                var plain = Control().Replace(screen.ToString(), "");
                foreach (var (prompt, keys) in answers)
                    if (plain.Contains(prompt, StringComparison.OrdinalIgnoreCase) && pressed.Add(prompt)) { await input.WriteAsync(Encoding.UTF8.GetBytes(keys)); await input.FlushAsync(); }
            }
        }
        // 伪控制台被关、管道断开：读到头了。
        catch (IOException) { }
        // 释放时流已关闭：读到头了。
        catch (ObjectDisposedException) { }
    }

    /// <summary>关伪控制台、释放属性表与环境块、关进程与线程句柄；只放非托管资源。</summary>
    private void Release()
    {
        if (console != IntPtr.Zero) ClosePseudoConsole(console);
        if (attributes != IntPtr.Zero) { DeleteProcThreadAttributeList(attributes); Marshal.FreeHGlobal(attributes); }
        if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
        if (threadHandle != IntPtr.Zero) CloseHandle(threadHandle);
        if (processHandle != IntPtr.Zero) CloseHandle(processHandle);
    }

    /// <summary>按 Windows 命令行规则拼程序与参数（空参数写成 <c>""</c>，含空格或引号的加引号转义）；<c>.cmd</c>、<c>.bat</c> 经 <c>cmd.exe /d /s /c</c> 跑。</summary>
    internal static string CommandLine(ProcessStartInfo start)
    {
        var line = string.Join(' ', new[] { start.FileName }.Concat(start.ArgumentList).Select(Quote));
        return start.FileName.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || start.FileName.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
            ? $"cmd.exe /d /s /c \"{line}\"" : line;
    }

    /// <summary>按 Windows 命令行规则给一个参数加引号：反斜杠只在引号前或结尾处加倍，引号前加反斜杠。</summary>
    private static string Quote(string argument)
    {
        if (argument.Length > 0 && !argument.Any(c => c is ' ' or '\t' or '"')) return argument;
        var quoted = new StringBuilder("\""); var slashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\') { slashes++; continue; }
            quoted.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c); slashes = 0;
        }
        return quoted.Append('\\', slashes * 2).Append('"').ToString();
    }

    /// <summary>终端控制序列（CSI、OSC 与两字符转义），读屏找提示前去掉。</summary>
    [GeneratedRegex(@"\x1b\[[0-9;?<>=]*[ -/]*[@-~]|\x1b\][^\x07\x1b]*(\x07|\x1b\\)|\x1b[@-Z\\-_]", RegexOptions.CultureInvariant)]
    private static partial Regex Control();

    /// <summary>对应 Win32 的 COORD：伪控制台的列数与行数。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Size { public short X, Y; }

    /// <summary>对应 Win32 的 STARTUPINFOW。</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInformation
    {
        public int Size; public string? Reserved, Desktop, Title; public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2Size; public IntPtr Reserved2, StandardInput, StandardOutput, StandardError;
    }

    /// <summary>对应 Win32 的 STARTUPINFOEXW：多一张属性表，用来挂伪控制台。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx { public StartupInformation StartupInfo; public IntPtr AttributeList; }

    /// <summary>对应 Win32 的 PROCESS_INFORMATION。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public IntPtr Process, Thread; public int ProcessId, ThreadId; }

    /// <summary>kernel32 的 CreatePipe：建一对匿名管道。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attributes, int size);

    /// <summary>kernel32 的 CreatePseudoConsole：建伪控制台，返回 HRESULT。</summary>
    [DllImport("kernel32.dll")]
    private static extern int CreatePseudoConsole(Size size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr console);

    /// <summary>kernel32 的 ClosePseudoConsole：关伪控制台，里面还在跑的程序随之结束。</summary>
    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(IntPtr console);

    /// <summary>kernel32 的 InitializeProcThreadAttributeList：先问大小，再在给定内存里建属性表。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    /// <summary>kernel32 的 UpdateProcThreadAttribute：往属性表里放伪控制台句柄。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);

    /// <summary>kernel32 的 DeleteProcThreadAttributeList：释放属性表内容。</summary>
    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    /// <summary>kernel32 的 CreateProcessW：按属性表与 Unicode 环境块起进程。</summary>
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateProcessW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string? application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInformation information);

    /// <summary>kernel32 的 CloseHandle。</summary>
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
