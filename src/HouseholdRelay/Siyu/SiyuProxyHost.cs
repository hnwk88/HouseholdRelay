using System.Net;
using System.Security.Cryptography.X509Certificates;
using Core.Configuration;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace HouseholdRelay.Siyu;

/// <summary>
/// 思域代理入口的宿主：独立的 Kestrel，监听所有网卡上的 <see cref="RelayConfiguration.SiyuProxyPort"/>，外层 TLS 用站点证书，ALPN 只报 http/1.1，
/// 每条连接交给组合根交进来的思域连接处理（CONNECT 认证、内层 TLS、逐请求准入都在那里）。不处理任何 HTTP 请求；宿主层只认 ASP.NET 的 <see cref="ConnectionHandler"/> 抽象。
/// </summary>
public sealed class SiyuProxyHost
{
    /// <summary>同时打开的代理连接上限：每人 8 个会话、每会话 16 条隧道是会话层的限额，这里只防连接洪水。</summary>
    internal const int MaxConnections = 256;

    private readonly RelayConfiguration _config;
    private readonly ConnectionHandler _handler;
    private readonly ILoggerFactory _loggers;

    /// <summary>三个依赖都必填；<paramref name="handler"/> 是思域代理入口的连接处理（由组合根指定）；<paramref name="loggers"/> 是所在容器的日志工厂，代理宿主共用同一份文件日志。</summary>
    public SiyuProxyHost(RelayConfiguration config, ConnectionHandler handler, ILoggerFactory loggers)
    {
        ArgumentNullException.ThrowIfNull(config); ArgumentNullException.ThrowIfNull(handler); ArgumentNullException.ThrowIfNull(loggers);
        _config = config; _handler = handler; _loggers = loggers;
    }

    /// <summary>构建代理宿主（只构建不启动）。<paramref name="testPort"/> 只给测试用：改为监听本机回环的这个端口（0 表示随机）。</summary>
    public WebApplication Build(X509Certificate2 certificate, X509Certificate2Collection? certificateChain, int? testPort = null)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Host.UseDefaultServiceProvider(options => { options.ValidateOnBuild = true; options.ValidateScopes = true; });
        // 日志工厂与连接处理都来自所在容器：会话服务、代理 CA 和出口只有一份，代理宿主不另起一套。
        builder.Services.AddSingleton(_loggers);
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false; options.Limits.MaxConcurrentConnections = MaxConnections;
            void Configure(ListenOptions listen)
            {
                // 外层只说 HTTP/1.1：ALPN 只报 http/1.1，客户端才会在外层发 CONNECT 而不是 HTTP/2。
                listen.Protocols = HttpProtocols.Http1;
                listen.UseHttps(tls => { tls.ServerCertificate = certificate; tls.ServerCertificateChain = certificateChain; });
                listen.Run(_handler.OnConnectedAsync);
            }
            if (testPort is not null) options.Listen(IPAddress.Loopback, testPort.Value, Configure);
            else options.ListenAnyIP(_config.SiyuProxyPort, Configure);
        });
        return builder.Build();
    }
}
