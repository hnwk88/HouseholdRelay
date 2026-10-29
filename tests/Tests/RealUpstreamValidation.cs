using System.Diagnostics;
using System.Text.Json;

namespace Tests;

/// <summary>仅显式调用时，取与真实探测相同的两家上游材料，通过管道交给真实浏览器做私有 HTTPS、原生客户端、用量与重启的完整验证，不输出凭据。</summary>
static class RealUpstreamValidation
{
    /// <summary>
    /// 仅在命令行首参为 --real-upstream-browser 时运行的真实上游验证入口：参数为材料来源（现有实例的 relay.db 或经用户同意的 MyCoAI account-vault.json）、
    /// 发布 DLL 和证据目录，可选 --client-only。材料按 <see cref="RealDetectionValidation.Providers"/> 只读取出，连同本机 claude.exe 路径经标准输入交给
    /// real-upstream-browser.cjs，凭据不写入命令行或输出；返回浏览器脚本的退出码。
    /// </summary>
    public static async Task<int> Run(string[] args)
    {
        if (args.Length is < 4 or > 5 || args.Length == 5 && args[4] != "--client-only")
            throw new ArgumentException("需要材料来源（relay.db 或 account-vault.json）、发布 DLL 和证据目录；可选 --client-only 仅验证客户端文件信任。");
        var material = new { providers = RealDetectionValidation.Providers(args[1]), claude = TestProcess.ClientLauncher.FindClaude() };
        var start = new ProcessStartInfo("node") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true };
        foreach (var argument in new[] { Path.GetFullPath("tests/Tests/real-upstream-browser.cjs"), Path.GetFullPath(args[2]), Path.GetFullPath(args[3]) }) start.ArgumentList.Add(argument);
        if (args.Length == 5) start.ArgumentList.Add(args[4]);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("无法启动浏览器验证。"); child.PriorityClass = ProcessPriorityClass.BelowNormal;
        await child.StandardInput.WriteAsync(JsonSerializer.Serialize(material, ConfigurationFiles.Json)); child.StandardInput.Close();
        await child.WaitForExitAsync(); return child.ExitCode;
    }
}
