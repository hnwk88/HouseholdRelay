using System.Diagnostics;
using System.Text.Json;

namespace Tests;

/// <summary>仅显式调用时，从经用户同意的 MyCoAI 账户库只读取出 sftoken 的地址与密钥，经标准输入交给浏览器脚本，在两套临时实例之间经网页导出、导入，再在新实例上测试、切换并用真实 Claude Code 调一次。</summary>
static class RealTransferValidation
{
    /// <summary>
    /// 命令行首参为 --real-transfer-browser 时运行：参数为 MyCoAI account-vault.json（或现有实例的 relay.db）、发布 DLL 和证据目录。
    /// 按 <see cref="RealDetectionValidation.Providers"/> 取出 sftoken 一家，连同本机 claude.exe 路径通过标准输入交给 real-transfer-browser.cjs；返回脚本的退出码。
    /// </summary>
    public static async Task<int> Run(string[] args)
    {
        if (args.Length != 4) throw new ArgumentException("需要 MyCoAI 账户库（或现有实例的 relay.db）、发布 DLL 和证据目录。");
        var sftoken = RealDetectionValidation.Providers(args[1], "sftoken").Single();
        var material = new { providers = new[] { sftoken }, claude = TestProcess.ClientLauncher.FindClaude() };
        var start = new ProcessStartInfo("node") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true };
        foreach (var argument in new[] { Path.GetFullPath("tests/Tests/real-transfer-browser.cjs"), Path.GetFullPath(args[2]), Path.GetFullPath(args[3]) }) start.ArgumentList.Add(argument);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("无法启动浏览器验证。"); child.PriorityClass = ProcessPriorityClass.BelowNormal;
        await child.StandardInput.WriteAsync(JsonSerializer.Serialize(material, ConfigurationFiles.Json)); child.StandardInput.Close();
        await child.WaitForExitAsync(); return child.ExitCode;
    }
}
