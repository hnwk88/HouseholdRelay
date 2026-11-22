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
    private readonly ILoggerFactory _loggers;

    /// <summary>三个依赖都必填，构造时当场判空。<paramref name="loggers"/> 是所在容器的日志工厂，对外宿主共用它：同一份文件日志、同一个最低级别。</summary>
    public RelayServer(RelayConfiguration config, IRelayForwarder forwarder, ILoggerFactory loggers)
    {
        ArgumentNullException.ThrowIfNull(config); ArgumentNullException.ThrowIfNull(forwarder); ArgumentNullException.ThrowIfNull(loggers);
        _config = config; _forwarder = forwarder; _loggers = loggers;
    }

    /// <summary>对外 HTTPS 中转宿主是否已在本进程启动；只由 serve 命令在启动成功后置为真，管理页据此提示新证书是否需要重启。</summary>
    public bool HttpsActive { get; internal set; }

    /// <summary>构建对外中转宿主（只构建不启动，由调用方启动）：正式服务必须提供证书并监听所有网卡上的 <see cref="RelayConfiguration.ListenPort"/>；只有测试可传 <paramref name="testPort"/> 改为监听本机回环且允许不启用 HTTPS。限制请求体、请求头大小与超时；给了 <paramref name="siyuControl"/> 时 <c>/v1/siyu</c> 前缀交给它（思域控制面），其余请求交给 <see cref="Handle"/>。</summary>
    public WebApplication Build(X509Certificate2? certificate, int? testPort = null, X509Certificate2Collection? certificateChain = null, RequestDelegate? siyuControl = null)
    {
        if (certificate is null && testPort is null) throw new InvalidOperationException("正式服务必须启用 HTTPS。");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Host.UseDefaultServiceProvider(options => { options.ValidateOnBuild = true; options.ValidateScopes = true; });
        // 对外宿主的日志交给所在容器的日志工厂：Kestrel 的警告与错误进同一份 logs\errors.log（只记类型），不另起一套、也不清空。
        // 工厂是外部实例，对外宿主释放时不会释放它。
        builder.Services.AddSingleton(_loggers);
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
        var app = builder.Build();
        // 思域控制面与中转同一个站点 HTTPS 入口，按路径前缀分开；前缀之外的请求一律照旧交给转发。
        if (siyuControl is not null) app.Map(new PathString(Siyu.SiyuControl.Prefix), branch => branch.Run(siyuControl));
        app.Run(Handle); return app;
    }

    /// <summary>中转请求入口：对外宿主直接用，管理宿主把未匹配页面的请求兜底转到这里。只把请求包成交换端口交给转发服务。</summary>
    public Task Handle(HttpContext context) => _forwarder.Handle(new HttpRelayExchange(context));
}
