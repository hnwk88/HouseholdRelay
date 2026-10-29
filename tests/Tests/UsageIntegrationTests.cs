using System.Net;
using System.Text;
using HouseholdRelay;
using Microsoft.AspNetCore.Http;
using static Tests.TestSuite;
using static Tests.UsageFixture;

namespace Tests;

static class UsageIntegrationTests
{
    /// <summary>
    /// 验证用量身份归属、轮换、改名和个人查询（API 密钥与 OAuth 两种上游模式）：两人并发请求时记录各自归属、带当前上游版本且完整，
    /// 个人查询只看到自己的记录；上游未给用量时记为未提供且令牌数为空；轮换并改名后三条记录都归同一成员、显示新名并保留旧令牌编号；
    /// 旧令牌被拒；在个人查询中指定他人 user_id 返回 400；停用后个人查询和请求都返回 401 且不新增记录；
    /// 管理页显示“上游未提供”且不含令牌、提示或回答；仅凭管理员 Cookie 访问个人用量返回 401。
    /// </summary>
    public static async Task IdentityAndModes()
    {
        await using var upstream = await UpstreamFixture.Create(); await using var web = await WebFixture.Create();
        var service = web.Upstreams; var a = web.Identities.Create("甲"); var b = web.Identities.Create("乙");
        long total = 0;
        foreach (var mode in new[] { "api-key", "oauth" })
        {
            upstream.Handler = null;
            await upstream.Activate(service, options: upstream.Options(mode: mode, authentication: mode == "oauth" ? "bearer" : "x-api-key"));
            var version = service.ReadActive().Version;
            var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var arrivals = 0;
            upstream.Handler = async context =>
            {
                if (Interlocked.Increment(ref arrivals) == 2) both.TrySetResult();
                await both.Task.WaitAsync(context.RequestAborted);
                var input = context.Request.Query["case"] == "a" ? 10 : 20;
                await Json(context, new { input_tokens = input, output_tokens = input + 1 });
            };
            // 两人同时请求，模拟上游等两个请求都到达后才应答，确保两条记录交错进行时仍各自归属正确的成员。
            Check((await Task.WhenAll(Call(web, a.Token), Call(web, b.Token, "b"))).All(status => status == HttpStatusCode.OK));
            total += 2; var report = await Settled(web, total);
            Check(report.Rows.Take(2).All(row => row.UpstreamVersion == version && row.Completeness == "complete"));
            var personal = await Personal(web, a.Token);
            Check(personal.Rows.All(row => row.UserId == a.UserId && row.Tokens.Input == 10));
            var theirs = await Personal(web, b.Token);
            Check(theirs.Rows.All(row => row.UserId == b.UserId) && theirs.Rows.Where(row => row.Completeness == "complete").All(row => row.Tokens.Input == 20));
            upstream.Handler = context => Json(context, null);
            Check(await Call(web, b.Token) == HttpStatusCode.OK); total++;
            report = await Settled(web, total);
            Check(report.Rows[0].Completeness == "not_provided" && report.Rows[0].Tokens.Input is null);
        }
        var oldTokenId = web.Identities.Clients.Authenticate(a.Token)!.TokenId;
        var rotated = web.Identities.Rotate(a.UserId); web.Identities.Rename(a.UserId, "甲改名");
        Check(await Call(web, rotated.Token) == HttpStatusCode.OK); total++;
        var all = await Settled(web, total); var mine = await Personal(web, rotated.Token);
        Check(mine.Total == 3 && mine.Rows.All(row => row.DisplayName == "甲改名" && row.UserId == a.UserId));
        Check(mine.Rows.Select(row => row.TokenId).Distinct().Count() == 2 && mine.Rows.Any(row => row.TokenId == oldTokenId));
        Check(await Call(web, a.Token) == HttpStatusCode.Unauthorized);
        using (var forged = new HttpRequestMessage(HttpMethod.Get, web.Url + "/my-usage/data?user_id=" + b.UserId))
        { forged.Headers.Authorization = new("Bearer", rotated.Token); using var result = await web.Http.SendAsync(forged); Check(result.StatusCode == HttpStatusCode.BadRequest); }
        web.Identities.SetEnabled(a.UserId, false);
        using (var request = new HttpRequestMessage(HttpMethod.Get, web.Url + "/my-usage/data"))
        { request.Headers.Authorization = new("Bearer", rotated.Token); using var result = await web.Http.SendAsync(request); Check(result.StatusCode == HttpStatusCode.Unauthorized); }
        Check(await Call(web, rotated.Token) == HttpStatusCode.Unauthorized && web.Relay.Usage!.Query(new()).Total == total);
        await web.Login(); using var page = await web.Http.GetAsync(web.Url + "/Usage?UserId=" + b.UserId);
        var html = WebUtility.HtmlDecode(await page.Content.ReadAsStringAsync()); Check(page.IsSuccessStatusCode && html.Contains("上游未提供"), "Page " + page.StatusCode + ": " + System.Text.RegularExpressions.Regex.Match(html, "validation-summary-errors[\\s\\S]*?</div>").Value);
        Check(!html.Contains(a.Token) && !html.Contains(rotated.Token) && !html.Contains("fixture-private-prompt") && !html.Contains("fixture-private-answer"));
        using var cookieOnly = await web.Http.GetAsync(web.Url + "/my-usage/data"); Check(cookieOnly.StatusCode == HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// 验证用量分类、空值合计、残缺 JSON 与幂等完成：激活上游时的管理诊断不计入个人用量；上游未给用量时令牌数为空、记为不完整，
    /// 管理页显示“未知”而不是 0；推理、估算（count_tokens）、模型列表分类统计，模型列表即使上游自报用量也记 0，
    /// 健康检查、预热、静态页和无效令牌不产生记录；推理合计只累加已知值；重复完成不改变已完成的记录；
    /// 数据库中不含个人令牌、提示和回答。
    /// </summary>
    public static async Task CategoriesAndUnknown()
    {
        await using var upstream = await UpstreamFixture.Create(); await using var web = await WebFixture.Create();
        var user = web.Identities.Create("未知用量"); await upstream.Activate(web.Upstreams);
        Check(web.Relay.Usage!.Query(new()).Total == 0, "Administrative probes must not enter personal usage");
        upstream.Handler = context => context.Request.Path == "/v1/models"
            ? context.Response.WriteAsJsonAsync(new { data = Array.Empty<string>(), usage = new { input_tokens = 999, output_tokens = 999 } })
            : context.Request.Path == "/v1/messages/count_tokens" ? context.Response.WriteAsJsonAsync(new { input_tokens = 123 })
            : Json(context, context.Request.Query["case"].ToString() switch
            { "partial" => new { input_tokens = 15 } as object, "complete" => new { input_tokens = 2, output_tokens = 3, cache_read_input_tokens = 4, cache_creation_input_tokens = 5 }, _ => null });
        Check(await Call(web, user.Token, "missing") == HttpStatusCode.OK); await Settled(web, 1);
        var unknown = web.Relay.Usage.Query(new()); Check(unknown.Summary[0].Tokens.Input is null && unknown.Summary[0].Incomplete == 1);
        await web.Login(); var html = WebUtility.HtmlDecode(await web.Http.GetStringAsync(web.Url + "/Usage"));
        Check(html.Contains("<strong>未知</strong>") && !html.Contains("<strong>0</strong>"), System.Text.RegularExpressions.Regex.Match(html, "validation-summary-errors[\\s\\S]*?</div>").Value);
        await Call(web, user.Token, "partial"); await Call(web, user.Token, "complete");
        await Call(web, user.Token, route: "/v1/models"); await Call(web, user.Token, route: "/v1/messages/count_tokens");
        using var health = await web.Http.GetAsync(web.Url + "/healthz");
        using var head = new HttpRequestMessage(HttpMethod.Head, web.Url + "/api/hello"); head.Headers.Authorization = new("Bearer", user.Token);
        using var prewarm = await web.Http.SendAsync(head);
        using var staticPage = await web.Http.GetAsync(web.Url + "/my-usage");
        Check(await Call(web, "invalid") == HttpStatusCode.Unauthorized);
        var report = await Settled(web, 5); var inference = report.Summary.Single(row => row.Kind == "inference");
        Check(inference.Requests == 3 && inference.Incomplete == 2 && inference.Tokens == new UsageTokens(17, 3, 4, 5));
        Check(report.Summary.Single(row => row.Kind == "estimate").Tokens.Input == 123);
        Check(report.Summary.Single(row => row.Kind == "models").Tokens == new UsageTokens());
        var complete = report.Rows.Single(row => row.Tokens.Output == 3);
        await web.Relay.Usage.Finish(complete.RequestId, new("failed", true, 500, 500, 1, "partial", new(888, 999)));
        Check(web.Relay.Usage.Query(new()).Rows.Single(row => row.RequestId == complete.RequestId) == complete, "Duplicate completion must be idempotent");
        using (var connection = web.Database.Open())
        {
            var serialized = (string)RelayDatabase.Scalar(connection, null, "SELECT group_concat(model) FROM usage_requests")!;
            Check(!serialized.Contains("fixture-private-prompt"));
        }
        var bytes = Encoding.UTF8.GetString(File.ReadAllBytes(web.Database.Path));
        Check(!bytes.Contains(user.Token) && !bytes.Contains("fixture-private-answer") && !bytes.Contains("fixture-private-prompt"));
    }

    /// <summary>
    /// 验证已通过认证但被拒绝的请求也会留下记录且不发往上游：未配置上游时返回 503，上传超限返回 413，超出每分钟次数返回 429；
    /// 三条记录都是失败、未派发、令牌数为空，上游调用次数不变。
    /// </summary>
    public static async Task Rejections()
    {
        await using var upstream = await UpstreamFixture.Create();
        await using var web = await WebFixture.Create(config => { config.RequestsPerMinute = 1; config.MaxBodyBytes = 100; });
        var user = web.Identities.Create("拒绝用量");
        Check(await Call(web, user.Token) == HttpStatusCode.ServiceUnavailable);
        var first = await Settled(web, 1); Check(first.Rows[0].Outcome == "failed" && !first.Rows[0].Dispatched && first.Rows[0].Completeness == "not_applicable");
        await upstream.Activate(web.Upstreams); var calls = upstream.Calls.Count;
        Check(await Call(web, user.Token) == HttpStatusCode.RequestEntityTooLarge);
        Check(await Call(web, user.Token) == HttpStatusCode.TooManyRequests);
        var report = await Settled(web, 3);
        Check(report.Rows.All(row => !row.Dispatched && row.Outcome == "failed" && row.Tokens.Input is null));
        Check(upstream.Calls.Count == calls);
    }
}
