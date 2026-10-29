using System.Globalization;
using Application.Interfaces.Usage;
using Application.Services.Usage;
using Core.Identity;
using Core.Relay;
using Core.Usage;

namespace HouseholdRelay.Relay;

/// <summary>个人入口只认有效中转令牌，不认管理 Cookie，也不允许外部指定归属 ID。</summary>
internal static class PersonalUsage
{
    /// <summary>由 <see cref="RelayServer.Handle"/> 在鉴权前调用，处理个人用量入口：路径不属于本入口时返回 false 交回中转流程；页面与静态资源（/my-usage、/usage-personal.js、/site.css）无需令牌直接返回；/my-usage/data 必须带有效中转令牌，归属 ID 只取自令牌，查询参数只允许白名单键且每个键只能出现一次。只接受 GET；可预期的拒绝都抛 <see cref="RelayFailure"/>，由调用方统一写出错误响应。</summary>
    internal static async Task<bool> TryHandle(HttpContext context, ClientRegistry clients, IUsageService? store)
    {
        var path = context.Request.Path.Value;
        var asset = path switch { "/my-usage" => "my-usage.html", "/usage-personal.js" => "usage-personal.js", "/site.css" => "site.css", _ => null };
        if (asset is null && path != "/my-usage/data") return false;
        if (!HttpMethods.IsGet(context.Request.Method)) throw new RelayFailure(405, "Method not allowed.");
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
        if (asset is not null)
        {
            context.Response.ContentType = asset.EndsWith(".html", StringComparison.Ordinal) ? "text/html; charset=utf-8"
                : asset.EndsWith(".js", StringComparison.Ordinal) ? "text/javascript; charset=utf-8" : "text/css; charset=utf-8";
            await context.Response.SendFileAsync(Path.Combine(AppContext.BaseDirectory, "wwwroot", asset), context.RequestAborted);
            return true;
        }
        var client = clients.Authenticate(RelayServer.GetToken(context.Request)) ?? throw new RelayFailure(401, "Invalid or disabled relay token.");
        if (store is null) throw new RelayFailure(503, "Usage storage unavailable.");
        var query = context.Request.Query;
        if (query.Keys.Any(key => key is not ("from" or "until" or "kind" or "model" or "outcome" or "page" or "granularity" or "timezone"))
            || query.Any(item => item.Value.Count != 1)) throw new RelayFailure(400, "Invalid usage query; identity comes only from the relay token.");
        DateTimeOffset? Date(string key)
        {
            var value = query[key].ToString();
            if (value.Length == 0) return null;
            if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)) throw new RelayFailure(400, "Invalid query date.");
            return date;
        }
        var page = 1;
        if (query.ContainsKey("page") && !int.TryParse(query["page"], out page)) throw new RelayFailure(400, "Invalid query page.");
        var filter = new UsageFilter(client.Id, Date("from"), Date("until"), query["kind"], query["model"], query["outcome"], page,
            query.ContainsKey("granularity") ? query["granularity"].ToString() : "day", query.ContainsKey("timezone") ? query["timezone"].ToString() : "UTC");
        try { filter.Validate(); }
        // 筛选校验的中文提示是写给管理页的，这里统一转成 400 和固定英文消息，不向外部客户端透露校验细节。
        catch (InvalidOperationException) { throw new RelayFailure(400, "Invalid usage query."); }
        UsageReport report;
        try { report = store.Query(filter); }
        // 只把可恢复的存储故障（数据库异常、文件访问失败、记录状态不满足）转成 503，不暴露 SQL 或路径；其他异常继续上抛，由调用方按内部错误处理。
        catch (Exception error) when (UsageService.StorageError(error)) { throw new RelayFailure(503, "Usage storage unavailable."); }
        await context.Response.WriteAsJsonAsync(report, context.RequestAborted);
        return true;
    }
}
