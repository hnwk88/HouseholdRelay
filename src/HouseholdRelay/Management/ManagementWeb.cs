using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Application.DependencyInjection;
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
using Microsoft.AspNetCore.Mvc.RazorPages;
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
        builder.Logging.ClearProviders();
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
            options.Events.OnValidatePrincipal = context =>
            {
                var administrators = context.HttpContext.RequestServices.GetRequiredService<IAdministratorService>();
                if (!administrators.IsSessionValid(context.Principal?.FindFirstValue(SessionClaim))) context.RejectPrincipal();
                return Task.CompletedTask;
            };
        });
        builder.Services.AddOptions<CookieAuthenticationOptions>(Scheme).Configure<IRelayInstance>((options, instance) => options.Cookie.Name = "hr_admin_" + instance.InstanceId);
        builder.Services.AddAuthorization();
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
            catch
            {
                // 不向浏览器披露 SQL、文件路径或凭据，数据库故障始终关闭访问。
                if (context.Response.HasStarted) { context.Abort(); return; }
                context.Response.Clear(); context.Response.StatusCode = 503; context.Response.Headers.CacheControl = "no-store";
                context.Response.ContentType = "text/plain; charset=utf-8";
                await context.Response.WriteAsync("管理数据暂不可用，请检查本机数据目录或恢复备份。");
            }
        });
        app.UseStaticFiles(); app.UseRouting(); app.UseRateLimiter(); app.UseAuthentication(); app.UseAuthorization();
        app.MapRazorPages();
        app.MapFallback(relay.Handle);
        return app;
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
