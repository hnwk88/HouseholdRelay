using System.Net;
using System.Security.Cryptography.X509Certificates;
using Application.Interfaces.Relay;
using Core.Configuration;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace HouseholdRelay.Relay;

/// <summary>对外中转宿主的 HTTP 绑定：建 Kestrel 宿主，把每个请求包成交换端口交给 <see cref="IRelayForwarder"/>；转发编排不在这里。</summary>
public sealed class RelayServer : IHttpsListener
{
    private readonly RelayConfiguration _config;
    private readonly IRelayForwarder _forwarder;

    /// <summary>两个依赖都必填，构造时当场判空。</summary>
    public RelayServer(RelayConfiguration config, IRelayForwarder forwarder)
    {
        ArgumentNullException.ThrowIfNull(config); ArgumentNullException.ThrowIfNull(forwarder);
        _config = config; _forwarder = forwarder;
    }

    /// <summary>对外 HTTPS 中转宿主是否已在本进程启动；只由 serve 命令在启动成功后置为真，管理页据此提示新证书是否需要重启。</summary>
    public bool HttpsActive { get; internal set; }

    /// <summary>构建对外中转宿主（只构建不启动，由调用方启动）：正式服务必须提供证书并监听所有网卡上的 <see cref="RelayConfiguration.ListenPort"/>；只有测试可传 <paramref name="testPort"/> 改为监听本机回环且允许不启用 HTTPS。限制请求体、请求头大小与超时，所有请求交给 <see cref="Handle"/>。</summary>
    public WebApplication Build(X509Certificate2? certificate, int? testPort = null, X509Certificate2Collection? certificateChain = null)
    {
        if (certificate is null && testPort is null) throw new InvalidOperationException("正式服务必须启用 HTTPS。");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Host.UseDefaultServiceProvider(options => { options.ValidateOnBuild = true; options.ValidateScopes = true; });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false; options.Limits.MaxConcurrentConnections = 64;
            options.Limits.MaxRequestBodySize = _config.MaxBodyBytes;
            options.Limits.MaxRequestHeadersTotalSize = 16384;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
            options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
            void Configure(ListenOptions listen)
            {
                listen.Protocols = HttpProtocols.Http1AndHttp2;
                if (certificate is not null) listen.UseHttps(tls => { tls.ServerCertificate = certificate; tls.ServerCertificateChain = certificateChain; });
            }
            if (testPort is not null) options.Listen(IPAddress.Loopback, testPort.Value, Configure);
            else options.ListenAnyIP(_config.ListenPort, Configure);
        });
        var app = builder.Build(); app.Run(Handle); return app;
    }

    /// <summary>中转请求入口：对外宿主直接用，管理宿主把未匹配页面的请求兜底转到这里。只把请求包成交换端口交给转发服务。</summary>
    public Task Handle(HttpContext context) => _forwarder.Handle(new HttpRelayExchange(context));
}
