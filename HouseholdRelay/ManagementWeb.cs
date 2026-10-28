using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;

namespace HouseholdRelay;

/// <summary>仅在本机回环地址提供管理页面，不接受代理头扩展管理边界。</summary>
public static class ManagementWeb
{
    public const string Scheme = "RelayAdministrator";
    public const string SessionClaim = "relay_session";

    public static WebApplication Build(RelayDatabase database, IdentityService identities, RelayServer relay, int port = 8788)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        { Args = [], ApplicationName = typeof(ManagementWeb).Assembly.FullName, ContentRootPath = AppContext.BaseDirectory, EnvironmentName = "Production" });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false; options.Listen(IPAddress.Loopback, port);
            options.Limits.MaxRequestBodySize = 65536;
            options.Limits.MaxRequestHeadersTotalSize = 16384;
            options.Limits.MaxConcurrentConnections = 64;
        });
        builder.Services.AddSingleton(database);
        builder.Services.AddSingleton(identities);
        builder.Services.AddSingleton(new ConfigurationService(database));
        builder.Services.AddSingleton(new AdministratorService(database));
        builder.Services.AddRazorPages(options =>
        {
            options.Conventions.AuthorizeFolder("/");
            options.Conventions.AllowAnonymousToPage("/Login");
            options.Conventions.AllowAnonymousToPage("/Setup");
        });
        builder.Services.AddDataProtection().SetApplicationName("HouseholdRelay-" + database.InstanceId)
            .PersistKeysToFileSystem(new DirectoryInfo(System.IO.Path.Combine(database.State, "keys"))).ProtectKeysWithDpapi();
        builder.Services.AddAntiforgery(options =>
        {
            options.Cookie.Name = "hr_csrf_" + database.InstanceId;
            options.Cookie.HttpOnly = true; options.Cookie.SameSite = SameSiteMode.Strict;
            // 此端口只接受回环 HTTP；未来 HTTPS 管理入口会自动使用 Secure。
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        });
        builder.Services.AddAuthentication(Scheme).AddCookie(Scheme, options =>
        {
            options.LoginPath = "/Login"; options.AccessDeniedPath = "/Login";
            options.Cookie.Name = "hr_admin_" + database.InstanceId;
            options.Cookie.HttpOnly = true; options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.ExpireTimeSpan = TimeSpan.FromHours(8); options.SlidingExpiration = false;
            options.Events.OnValidatePrincipal = context =>
            {
                var administrators = context.HttpContext.RequestServices.GetRequiredService<AdministratorService>();
                if (!administrators.IsSessionValid(context.Principal?.FindFirstValue(SessionClaim))) context.RejectPrincipal();
                return Task.CompletedTask;
            };
        });
        builder.Services.AddAuthorization();
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                HttpMethods.IsPost(context.Request.Method) && (context.Request.Path.Equals("/Login", StringComparison.OrdinalIgnoreCase)
                    || context.Request.Path.Equals("/Setup", StringComparison.OrdinalIgnoreCase))
                ? RateLimitPartition.GetFixedWindowLimiter(context.Request.Path.Value!.ToLowerInvariant(), _ => new FixedWindowRateLimiterOptions
                    { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true })
                : RateLimitPartition.GetNoLimiter("other"));
            options.OnRejected = async (context, cancellation) =>
            {
                context.HttpContext.Response.Headers.RetryAfter = "60";
                context.HttpContext.Response.ContentType = "text/plain; charset=utf-8";
                await context.HttpContext.Response.WriteAsync("尝试过于频繁，请一分钟后重试。", cancellation);
            };
        });
        var app = builder.Build();
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
