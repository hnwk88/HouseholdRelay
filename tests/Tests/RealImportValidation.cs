using System.Diagnostics;
using System.Text.Json;

namespace Tests;

/// <summary>仅显式调用时，把一份新做的官方登录文件经网页「导入登录文件」接进临时实例，再用真实 Claude Code 客户端经中转调一次；文件只按路径交给浏览器脚本，不输出其中的令牌。</summary>
static class RealImportValidation
{
    /// <summary>
    /// 命令行首参为 --real-import-browser 时运行：参数为登录文件（临时配置目录里的 .credentials.json）、发布 DLL 和证据目录。
    /// 拒绝用户自己的 %USERPROFILE%\.claude 与正式数据目录 E:\RelayData 下的文件，免得和正在用的登录互相刷掉；把文件路径和本机 claude.exe 路径经标准输入交给 real-import-browser.cjs，返回它的退出码。
    /// </summary>
    public static async Task<int> Run(string[] args)
    {
        if (args.Length != 4) throw new ArgumentException("需要登录文件、发布 DLL 和证据目录。");
        var file = Path.GetFullPath(args[1]);
        string[] forbidden = [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"), @"E:\RelayData"];
        if (forbidden.Any(root => file.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("不能导入正在使用的登录文件，请在临时配置目录里新登录一次。");
        var material = new { file, claude = TestProcess.ClientLauncher.FindClaude() };
        var start = new ProcessStartInfo("node") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true };
        foreach (var argument in new[] { Path.GetFullPath("tests/Tests/real-import-browser.cjs"), Path.GetFullPath(args[2]), Path.GetFullPath(args[3]) }) start.ArgumentList.Add(argument);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("无法启动浏览器验证。"); child.PriorityClass = ProcessPriorityClass.BelowNormal;
        await child.StandardInput.WriteAsync(JsonSerializer.Serialize(material, ConfigurationFiles.Json)); child.StandardInput.Close();
        await child.WaitForExitAsync(); return child.ExitCode;
    }
}
