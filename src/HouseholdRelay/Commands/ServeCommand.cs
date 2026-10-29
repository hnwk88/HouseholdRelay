using System.Security.Cryptography;
using Application.Interfaces.Administration;
using Application.Interfaces.Certificates;
using Core.Certificates;
using Core.Configuration;
using HouseholdRelay.Relay;

namespace HouseholdRelay.Commands;

/// <summary>命令行 serve：加载证书、启动本机管理网页和对外 HTTPS 中转两个宿主，并在控制台给出引导信息。</summary>
public sealed class ServeCommand(RelayServer relay, RelayConfiguration config, ICertificateService certificates, IAdministratorService administrators)
{
    /// <summary>管理端口须在 1024 至 65535 之间；在打开数据目录前检查。</summary>
    public static void CheckPort(int adminPort)
    {
        if (adminPort is < 1024 or > 65535) throw new InvalidOperationException("管理端口应在 1024 至 65535 之间。");
    }

    /// <summary>启动已构建的管理宿主与（有证书时）中转宿主，直到管理宿主关闭。</summary>
    public async Task Run(WebApplication admin, int adminPort)
    {
        ServerCertificates? loaded = null;
        try { loaded = certificates.LoadForServing(); }
        // 证书损坏或配置不符时只降级为不启用对外 HTTPS，管理网页照常启动，让操作者能在本机重新导入证书。
        catch (Exception error) when (error is CryptographicException or InvalidOperationException)
        { Console.WriteLine("HTTPS 证书无法启用，请在本机管理页面检查证书链、域名和有效期后重新导入。"); }
        using var certificate = loaded;
        await using var api = certificate is null ? null : relay.Build(certificate.Certificate, certificateChain: certificate.Chain);
        await admin.StartAsync();
        if (api is not null) { await api.StartAsync(); relay.HttpsActive = true; }
        Console.WriteLine($"本机管理：http://127.0.0.1:{adminPort}；按 Ctrl+C 停止。");
        var bootstrap = administrators.BootstrapCode();
        if (bootstrap is not null) Console.WriteLine($"首次设置引导码（仅供本机输入）：{bootstrap}");
        Console.WriteLine(api is null ? "对外 HTTPS 未启用，请在本机网页创建或导入证书并重启。" : certificates.ReadInfo()?.Mode == "trusted"
            ? $"HTTPS 监听端口：{config.ListenPort}；请从实际客户端验证连接。"
            : $"私有 CA HTTPS 监听端口：{config.ListenPort}；请从连接页下载公共信任包，每台客户端安装一次。");
        await admin.WaitForShutdownAsync();
    }
}
