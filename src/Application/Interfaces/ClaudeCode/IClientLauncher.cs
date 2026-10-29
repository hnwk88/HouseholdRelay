using System.Diagnostics;

namespace Application.Interfaces.ClaudeCode;

/// <summary>定位本机原生 Claude Code，并以只对子进程生效的连接变量和信任证书启动它。</summary>
public interface IClientLauncher
{
    /// <summary>在 PATH、用户本地目录和 npm 全局目录中查找 claude.exe；找不到时提示安装官方客户端。</summary>
    string FindClaude();

    /// <summary>读取本机 Claude Code 版本并组成其请求特征字符串。</summary>
    Task<string> DetectUserAgent();

    /// <summary>按旧客户端包准备启动参数：清除冲突的凭据变量，只为子进程注入中转地址、令牌和 CA。</summary>
    ProcessStartInfo Prepare(string bundleDirectory, IEnumerable<string> arguments);

    /// <summary>启动客户端并等待退出，返回其退出码。</summary>
    Task<int> Run(string directory, string[] arguments);
}
