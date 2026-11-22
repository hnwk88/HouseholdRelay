using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.RateLimiting;
using Application.DependencyInjection;
using Application.Logging;
using Application.Interfaces.Administration;
using Application.Interfaces.Configuration;
using HouseholdRelay.Composition;
using HouseholdRelay.Relay;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;

namespace HouseholdRelay.Management;

/// <summary>仅在本机回环地址提供管理页面，不接受代理头扩展管理边界。</summary>
public static class ManagementWeb
{
    public const string Scheme = "RelayAdministrator";
    public const string SessionClaim = "relay_session";

    /// <summary>构建管理宿主：经组合根登记全部服务（数据目录为 <paramref name="home"/>），<paramref name="configureServices"/> 可追加或覆盖登记。</summary>
    public static WebApplication Build(string home, int port = 8788, Action<IServiceCollection>? configureServices = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        { Args = [], ApplicationName = typeof(ManagementWeb).Assembly.FullName, ContentRootPath = AppContext.BaseDirectory, EnvironmentName = "Production" });
        // 构建容器时就核对全部登记可解析、单例不持有作用域服务：漏登记在启动时报，不在第一个请求时静默降级。
        builder.Host.UseDefaultServiceProvider(options => { options.ValidateOnBuild = true; options.ValidateScopes = true; });
        // 日志不清空：框架默认的提供程序照常收警告，数据目录 logs\errors.log 由基础设施登记时挂上（只记类型、阶段、编号），最低级别定为 Warning。
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false; options.Listen(IPAddress.Loopback, port);
            options.Limits.MaxRequestBodySize = 2 * 1024 * 1024;
            options.Limits.MaxRequestHeadersTotalSize = 16384;
            options.Limits.MaxConcurrentConnections = 64;
        });
        builder.Services.AddHouseholdRelay(options => options.Home = home);
        builder.Services.AddApplicationBackgroundServices();
        configureServices?.Invoke(builder.Services);
        builder.Services.AddRazorPages(options =>
        {
            options.Conventions.AuthorizeFolder("/");
            options.Conventions.AllowAnonymousToPage("/Login");
            options.Conventions.AllowAnonymousToPage("/Setup");
        });
        // 应用名、密钥目录和 Cookie 名都按实例隔离；实例信息在容器构造数据库后才可用，所以经选项从容器读取。
        builder.Services.AddDataProtection().ProtectKeysWithDpapi();
        builder.Services.AddOptions<DataProtectionOptions>().Configure<IRelayInstance>((options, instance) =>
            options.ApplicationDiscriminator = "HouseholdRelay-" + instance.InstanceId);
        builder.Services.AddOptions<KeyManagementOptions>().Configure<IRelayInstance, ILoggerFactory>((options, instance, loggers) =>
            options.XmlRepository = new FileSystemXmlRepository(new DirectoryInfo(System.IO.Path.Combine(instance.State, "keys")), loggers));
        builder.Services.AddAntiforgery(options =>
        {
            options.Cookie.HttpOnly = true; options.Cookie.SameSite = SameSiteMode.Strict;
            // 此端口只接受回环 HTTP；未来 HTTPS 管理入口会自动使用 Secure。
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        });
        builder.Services.AddOptions<AntiforgeryOptions>().Configure<IRelayInstance>((options, instance) => options.Cookie.Name = "hr_csrf_" + instance.InstanceId);
        builder.Services.AddAuthentication(Scheme).AddCookie(Scheme, options =>
        {
            options.LoginPath = "/Login"; options.AccessDeniedPath = "/Login";
            options.Cookie.HttpOnly = true; options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.ExpireTimeSpan = TimeSpan.FromHours(8); options.SlidingExpiration = false;
            options.Events.OnValidatePrincipal = async context =>
            {
                var administrators = context.HttpContext.RequestServices.GetRequiredService<IAdministratorService>();
                if (!await administrators.IsSessionValid(context.Principal?.FindFirstValue(SessionClaim))) context.RejectPrincipal();
            };
        });
        builder.Services.AddOptions<CookieAuthenticationOptions>(Scheme).Configure<IRelayInstance>((options, instance) => options.Cookie.Name = "hr_admin_" + instance.InstanceId);
        builder.Services.AddAuthorization();
        // 就绪检查只挂在本机管理端口（只听回环）：库打不开、没配上游这类内部状态不在公网匿名暴露；对外的 /healthz 只说明进程活着，逐字不变。
        builder.Services.AddHealthChecks().AddCheck<DatabaseReadiness>("database").AddCheck<UpstreamReadiness>("upstream");
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                // UseRouting 已匹配页面；同一页面的大小写、尾斜杠和其他有效别名必须共用预算。
                var page = context.GetEndpoint()?.Metadata.GetMetadata<PageActionDescriptor>()?.ViewEnginePath;
                return HttpMethods.IsPost(context.Request.Method) && page is "/Login" or "/Setup"
                ? RateLimitPartition.GetFixedWindowLimiter(page, _ => new FixedWindowRateLimiterOptions
                    { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true })
                : RateLimitPartition.GetNoLimiter("other");
            });
            options.OnRejected = async (context, cancellation) =>
            {
                context.HttpContext.Response.Headers.RetryAfter = "60";
                context.HttpContext.Response.ContentType = "text/plain; charset=utf-8";
                await context.HttpContext.Response.WriteAsync("尝试过于频繁，请一分钟后重试。", cancellation);
            };
        });
        var app = builder.Build();
        // 构建宿主时即从容器取中转端点：数据库在此打开、设置在此读取，故障在启动前暴露，与迁移前一致。
        var relay = app.Services.GetRequiredService<RelayServer>();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("HouseholdRelay.Management");
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            // 同源表单保留 Origin；no-referrer 会使 Chromium 的导航 POST 产生 Origin: null。
            context.Response.Headers["Referrer-Policy"] = "same-origin";
            context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
            if (!IsLocalRequest(context))
            { context.Response.StatusCode = 403; context.Response.ContentType = "text/plain; charset=utf-8"; await context.Response.WriteAsync("管理入口只接受本机同源访问。"); return; }
            try { await next(context); }
            catch (Exception error)
            {
                // 不向浏览器披露 SQL、文件路径或凭据，数据库故障始终关闭访问；日志只记异常类型链、阶段与这次请求的编号，事后能对上是哪一次、哪一类故障。
                RelayLog.Unexpected(logger, error, "management", context.TraceIdentifier);
                if (context.Response.HasStarted) { context.Abort(); return; }
                context.Response.Clear(); context.Response.StatusCode = 503; context.Response.Headers.CacheControl = "no-store";
                context.Response.ContentType = "text/plain; charset=utf-8";
                await context.Response.WriteAsync("管理数据暂不可用，请检查本机数据目录或恢复备份。");
            }
        });
        app.UseStaticFiles(); app.UseRouting(); app.UseRateLimiter(); app.UseAuthentication(); app.UseAuthorization();
        app.MapRazorPages();
        app.MapHealthChecks(ReadinessPath, new HealthCheckOptions { ResponseWriter = WriteReadiness, AllowCachingResponses = false }).AllowAnonymous();
        app.MapFallback(relay.Handle);
        return app;
    }

    /// <summary>本机就绪检查的路径：只在管理端口上有，对外中转端口没有这个路由。</summary>
    public const string ReadinessPath = "/readyz";

    /// <summary>就绪检查的读法：一行 JSON，总结论加每一项的结论与中文读句，中文不转义；不带异常细节。未就绪时状态码 503（框架按结论给）。</summary>
    private static Task WriteReadiness(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        var body = new
        {
            status = report.Status == HealthStatus.Healthy ? "ready" : "not_ready",
            checks = report.Entries.Select(entry => new { name = entry.Key, ready = entry.Value.Status == HealthStatus.Healthy, text = entry.Value.Description }).ToArray(),
        };
        return context.Response.WriteAsync(JsonSerializer.Serialize(body, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }

    /// <summary>管理入口的本机同源判定，由 <see cref="Build"/> 中的首个中间件调用，不通过即回 403：要求对端为回环地址、Host 只能是 localhost 或 127.0.0.1 且端口与实际监听端口一致（挡住 DNS 重绑定），带 Origin 时必须与本页同源，拒绝 Sec-Fetch-Site 为 cross-site 的请求；/v1 以外的 POST 还必须携带精确 Origin，作为防 CSRF 令牌之外的第二道防线。</summary>
    private static bool IsLocalRequest(HttpContext context)
    {
        var request = context.Request; var host = request.Host.Host;
        if (context.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address)
            || !(host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host == "127.0.0.1")
            || request.Host.Port != context.Connection.LocalPort) return false;
        var origin = request.Headers.Origin.ToString();
        var expected = request.Scheme + "://" + request.Host.Value;
        if (origin.Length != 0 && !string.Equals(origin, expected, StringComparison.OrdinalIgnoreCase)) return false;
        if (request.Headers["Sec-Fetch-Site"] == "cross-site") return false;
        // 浏览器表单必须携带精确 Origin；模型 API 的认证仍由独立中转令牌完成。
        var managementPost = HttpMethods.IsPost(request.Method) && !request.Path.StartsWithSegments("/v1");
        return !managementPost || string.Equals(origin, expected, StringComparison.OrdinalIgnoreCase);
    }
}
