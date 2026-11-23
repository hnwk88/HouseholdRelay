using System.Security.Cryptography;
using Application.Interfaces.Administration;
using Application.Logging;
using Application.Interfaces.Certificates;
using Core.Certificates;
using Core.Configuration;
using HouseholdRelay.Relay;

namespace HouseholdRelay.Commands;

/// <summary>命令行 serve：加载证书、启动本机管理网页和对外 HTTPS 中转两个宿主，并在控制台给出引导信息。</summary>
public sealed class ServeCommand(RelayServer relay, RelayConfiguration config, ICertificateService certificates, IAdministratorService administrators, Application.Interfaces.Configuration.IRelayInstance instance,
    Lazy<Siyu.SiyuControl> siyuControl, Lazy<Siyu.SiyuProxyHost> siyuProxy, Siyu.SiyuHostState siyuState, ILogger<ServeCommand> logger)
{
    /// <summary>管理端口须在 1024 至 65535 之间；在打开数据目录前检查。</summary>
    public static void CheckPort(int adminPort)
    {
        if (adminPort is < 1024 or > 65535) throw new InvalidOperationException("管理端口应在 1024 至 65535 之间。");
    }

    /// <summary>启动已构建的管理宿主与（有证书时）中转宿主，直到管理宿主关闭。</summary>
    public async Task RunAsync(WebApplication admin, int adminPort)
    {
        ServerCertificates? loaded = null;
        try { loaded = await certificates.LoadForServingAsync(); }
        // 证书损坏或配置不符时只降级为不启用对外 HTTPS，管理网页照常启动，让操作者能在本机重新导入证书。
        // 控制台读句不变；具体是哪一类失败记进 logs\errors.log（只有类型链与阶段），管理员事后查得到。
        catch (Exception error) when (error is CryptographicException or InvalidOperationException)
        { RelayLog.Unexpected(logger, error, "serve:certificate"); Console.WriteLine("HTTPS 证书无法启用，请在本机管理页面检查证书链、域名和有效期后重新导入。"); }
        using var certificate = loaded;
        await admin.StartAsync();
        if (certificate is null && config.SiyuEnabled) siyuState.Problem = "对外 HTTPS 证书没有启用，思域代理跟着没有起来。请在 HTTPS 入口页创建或导入证书后重启。";
        await using var proxy = certificate is null || !config.SiyuEnabled ? null : await StartSiyuAsync(certificate);
        siyuState.Active = proxy is not null;
        RequestDelegate siyu = proxy is null ? Siyu.SiyuControl.DisabledAsync : siyuControl.Value.HandleAsync;
        await using var api = certificate is null ? null : relay.Build(certificate.Certificate, certificateChain: certificate.Chain, siyuControl: siyu);
        if (api is not null) { await api.StartAsync(); relay.HttpsActive = true; }
        Console.WriteLine($"本机管理：http://127.0.0.1:{adminPort}；按 Ctrl+C 停止。");
        // 只在这次启动刚升级过时读；之前的升级记录留在管理首页。
        if (instance.UpgradedOnOpen && instance.LastUpgrade is { } upgrade) Console.WriteLine(upgrade.Sentence());
        var bootstrap = await administrators.BootstrapCodeAsync();
        if (bootstrap is not null) Console.WriteLine($"首次设置引导码（仅供本机输入）：{bootstrap}");
        var mode = api is null ? null : (await certificates.ReadInfoAsync())?.Mode;
        Console.WriteLine(api is null ? "对外 HTTPS 未启用，请在本机网页创建或导入证书并重启。" : mode == "trusted"
            ? $"HTTPS 监听端口：{config.ListenPort}；请从实际客户端验证连接。"
            : $"私有 CA HTTPS 监听端口：{config.ListenPort}；请从连接页下载公共信任包，每台客户端安装一次。");
        if (proxy is not null) Console.WriteLine($"思域代理端口：{config.SiyuProxyPort}；路由器要按同一个端口号转发到本机。");
        await admin.WaitForShutdownAsync();
    }

    /// <summary>
    /// 开了思域时：先构造控制面（会话服务随之构造，订阅令牌名单与账户退役），再起代理宿主。没填对外地址、代理 CA 读写失败或端口被占用时不启用思域，
    /// 读出原因、记进 logs\errors.log，返回 null；管理页与中转照常启动，控制面回「还没有打开思域」。
    /// </summary>
    private async Task<WebApplication?> StartSiyuAsync(ServerCertificates certificate)
    {
        WebApplication? proxy = null;
        try
        {
            _ = siyuControl.Value;
            proxy = siyuProxy.Value.Build(certificate.Certificate, certificate.Chain);
            await proxy.StartAsync();
            return proxy;
        }
        // 没填对外地址：读句本身就是给管理员的下一步。
        catch (InvalidOperationException error) { RelayLog.Unexpected(logger, error, "serve:siyu"); siyuState.Problem = error.Message; Console.WriteLine("思域没有启用：" + error.Message); }
        // 端口被占用、代理 CA 存取失败：原始消息可能带路径，只给归类后的下一步。
        catch (Exception error) when (error is IOException or CryptographicException or UnauthorizedAccessException)
        { RelayLog.Unexpected(logger, error, "serve:siyu"); siyuState.Problem = $"代理端口 {config.SiyuProxyPort} 打不开或代理证书读写失败。请检查端口占用，或在项目设置里换一个思域代理端口后重启。"; Console.WriteLine("思域没有启用：" + siyuState.Problem); }
        // 启动失败的代理宿主要释放，不留半开的监听。
        if (proxy is not null) await proxy.DisposeAsync();
        return null;
    }
}
