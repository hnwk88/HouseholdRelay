using System.Diagnostics;

namespace Application.Interfaces.ClaudeCode;

/// <summary>定位本机原生 Claude Code，并以只对子进程生效的连接变量和信任证书启动它。</summary>
public interface IClientLauncher
{
    /// <summary>
    /// 依次在 PATH 各目录、<c>%USERPROFILE%\.local\bin</c>、<c>%APPDATA%\npm</c> 里找 <c>claude.exe</c>，每个目录同时找 <c>node_modules\@anthropic-ai\claude-code\bin\claude.exe</c>
    /// （npm 全局安装的原生客户端在这里，npm 目录本身没有 claude.exe）；找不到时提示安装官方客户端。
    /// </summary>
    string FindClaude();

    /// <summary>读取本机 Claude Code 版本并组成其请求特征字符串。</summary>
    Task<string> DetectUserAgent();

    /// <summary>按旧客户端包准备启动参数：清除冲突的凭据变量，只为子进程注入中转地址、令牌和 CA。</summary>
    ProcessStartInfo Prepare(string bundleDirectory, IEnumerable<string> arguments);

    /// <summary>启动客户端并等待退出，返回其退出码。</summary>
    Task<int> Run(string directory, string[] arguments);
}
