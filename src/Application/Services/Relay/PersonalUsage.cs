using System.Globalization;
using Application.Interfaces.Relay;
using Application.Interfaces.Usage;
using Application.Services.Usage;
using Core.Identity;
using Core.Relay;
using Core.Usage;
using Microsoft.Extensions.Primitives;

namespace Application.Services.Relay;

/// <summary>个人入口只认有效中转令牌，不认管理 Cookie，也不允许外部指定归属 ID。</summary>
internal static class PersonalUsage
{
    /// <summary>由 <see cref="RelayForwarder.Handle"/> 在鉴权前调用，处理个人用量入口：路径不属于本入口时返回 false 交回中转流程；页面与静态资源（/my-usage、/usage-personal.js、/site.css）和取值标签表（/my-usage/labels）无需令牌直接返回；/my-usage/data 必须带有效中转令牌，归属 ID 只取自令牌，查询参数只允许白名单键且每个键只能出现一次。只接受 GET；可预期的拒绝都抛 <see cref="RelayFailure"/>，由调用方统一写出错误响应。独立中转没有用量服务（<paramref name="store"/> 为空），数据接口返回 503。</summary>
    internal static async Task<bool> TryHandle(IRelayExchange exchange, ClientRegistry clients, IUsageService? store)
    {
        var path = exchange.Path;
        var asset = path switch { "/my-usage" => "my-usage.html", "/usage-personal.js" => "usage-personal.js", "/site.css" => "site.css", _ => null };
        if (asset is null && path is not ("/my-usage/data" or "/my-usage/labels")) return false;
        if (!string.Equals(exchange.Method, "GET", StringComparison.OrdinalIgnoreCase)) throw new RelayFailure(405, "Method not allowed.");
        var reply = exchange.Reply;
        reply.SetHeader("Cache-Control", ["no-store"]);
        reply.SetHeader("X-Content-Type-Options", ["nosniff"]);
        reply.SetHeader("Referrer-Policy", ["no-referrer"]);
        reply.SetHeader("Content-Security-Policy", ["default-src 'self'; script-src 'self'; style-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'"]);
        if (asset is not null)
        {
            reply.ContentType = asset.EndsWith(".html", StringComparison.Ordinal) ? "text/html; charset=utf-8"
                : asset.EndsWith(".js", StringComparison.Ordinal) ? "text/javascript; charset=utf-8" : "text/css; charset=utf-8";
            await reply.SendFile(Path.Combine(AppContext.BaseDirectory, "wwwroot", asset), exchange.Aborted);
            return true;
        }
        // 标签表只是取值的中文名（接口类别、结果、币种等），与任何人的用量无关，不要令牌；个人页打开时据此填筛选选项，不另抄一份。
        if (path == "/my-usage/labels") { await reply.WriteJson(UsageLabelTable.Instance, exchange.Aborted); return true; }
        var client = await clients.Authenticate(RelayToken.From(exchange)) ?? throw new RelayFailure(401, "Invalid or disabled relay token.");
        // 只按库里的使用者主键圈定；没有主键的鉴权项（旧内存夹具）不能查，免得筛选条件为空读成全家的用量。
        if (store is null || client.UserId is not { } self) throw new RelayFailure(503, "Usage storage unavailable.");
        var query = exchange.Query;
        StringValues Value(string key) => query.GetValueOrDefault(key);
        if (query.Keys.Any(key => key is not ("from" or "until" or "kind" or "model" or "outcome" or "page" or "granularity" or "timezone"))
            || query.Any(item => item.Value.Count != 1)) throw new RelayFailure(400, "Invalid usage query; identity comes only from the relay token.");
        DateTimeOffset? Date(string key)
        {
            var value = Value(key).ToString();
            if (value.Length == 0) return null;
            if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)) throw new RelayFailure(400, "Invalid query date.");
            return date;
        }
        var page = 1;
        if (query.ContainsKey("page") && !int.TryParse(Value("page"), out page)) throw new RelayFailure(400, "Invalid query page.");
        var from = Date("from"); var until = Date("until");
        UsageKind? kind; UsageOutcome? outcome;
        try { kind = UsageFilter.Closed<UsageKind>(Value("kind")); outcome = UsageFilter.Closed<UsageOutcome>(Value("outcome")); }
        // 类别或结果不认识：和筛选校验同一口径，统一转成 400 和固定英文消息。
        catch (InvalidOperationException) { throw new RelayFailure(400, "Invalid usage query."); }
        var filter = new UsageFilter(self, from, until, kind, Value("model"), outcome, page,
            query.ContainsKey("granularity") ? Value("granularity").ToString() : "day", query.ContainsKey("timezone") ? Value("timezone").ToString() : "UTC");
        try { filter.Validate(); }
        // 筛选校验的中文提示是写给管理页的，这里统一转成 400 和固定英文消息，不向外部客户端透露校验细节。
        catch (InvalidOperationException) { throw new RelayFailure(400, "Invalid usage query."); }
        UsageReport report;
        try { report = await store.Query(filter); }
        // 只把可恢复的存储故障（数据库异常、文件访问失败、记录状态不满足）转成 503，不暴露 SQL 或路径；其他异常继续上抛，由调用方按内部错误处理。
        catch (Exception error) when (UsageService.StorageError(error)) { throw new RelayFailure(503, "Usage storage unavailable."); }
        await reply.WriteJson(report, exchange.Aborted);
        return true;
    }
}
