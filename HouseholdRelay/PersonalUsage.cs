using System.Globalization;

namespace HouseholdRelay;

/// <summary>个人入口只认有效中转令牌，不认管理 Cookie，也不允许外部指定归属 ID。</summary>
internal static class PersonalUsage
{
    internal static async Task<bool> TryHandle(HttpContext context, ClientRegistry clients, UsageStore? store)
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
        catch (InvalidOperationException) { throw new RelayFailure(400, "Invalid usage query."); }
        UsageReport report;
        try { report = store.Query(filter); }
        catch (Exception error) when (UsageStore.StorageError(error)) { throw new RelayFailure(503, "Usage storage unavailable."); }
        await context.Response.WriteAsJsonAsync(report, context.RequestAborted);
        return true;
    }
}
