using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HouseholdRelay;
using Microsoft.AspNetCore.Http;
using static Tests.TestSuite;

namespace Tests;

/// <summary>网络请求只带虚构个人令牌；断言读取已结束的数据库记录。</summary>
static class UsageFixture
{
    /// <summary>
    /// 构造发往中转的请求：/v1/models 用 GET，其余用 POST；查询串带 case=场景名，供模拟上游选择应答方式；
    /// 默认请求体含虚构提示和一个伪造的 metadata.user_id，模拟客户端自报的身份信息。调用方负责释放。
    /// </summary>
    public static HttpRequestMessage Request(WebFixture web, string token, string scenario = "a", string route = "/v1/messages", string? body = null)
    {
        var request = new HttpRequestMessage(route == "/v1/models" ? HttpMethod.Get : HttpMethod.Post, web.Url + route + "?case=" + scenario);
        request.Headers.Authorization = new("Bearer", token);
        if (request.Method == HttpMethod.Post) request.Content = new StringContent(body ?? """
            {"model":"fixture-model","messages":[{"role":"user","content":"fixture-private-prompt"}],"metadata":{"user_id":"forged-other-owner"}}
            """, Encoding.UTF8, "application/json");
        return request;
    }
    /// <summary>发送一次请求并读完响应后释放，只返回状态码。</summary>
    public static async Task<HttpStatusCode> Call(WebFixture web, string token, string scenario = "a", string route = "/v1/messages")
    {
        using var request = Request(web, token, scenario, route);
        using var response = await web.Http.SendAsync(request);
        return response.StatusCode;
    }
    /// <summary>等待用量记录总数达到 count 且没有待写入的记录，再返回全量报表；约 2 秒内未收敛即判失败。</summary>
    public static async Task<UsageReport> Settled(WebFixture web, long count)
    {
        await Until(() => { var report = web.Relay.Usage!.Query(new()); return report.Total == count && report.Summary.All(row => row.Pending == 0); });
        return web.Relay.Usage!.Query(new());
    }
    /// <summary>以个人令牌查询 /my-usage/data，要求成功并返回反序列化后的个人用量报表。</summary>
    public static async Task<UsageReport> Personal(WebFixture web, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, web.Url + "/my-usage/data"); request.Headers.Authorization = new("Bearer", token);
        using var response = await web.Http.SendAsync(request); Check(response.IsSuccessStatusCode);
        return (await response.Content.ReadFromJsonAsync<UsageReport>())!;
    }
    /// <summary>模拟上游写出一条非流式 JSON 消息，usage 字段按测试传入（可为空或残缺），用于验证用量解析。</summary>
    public static Task Json(HttpContext context, object? usage) => context.Response.WriteAsJsonAsync(new
    { type = "message", role = "assistant", model = "fixture-model", content = new[] { new { type = "text", text = "fixture-private-answer" } }, stop_reason = "end_turn", usage });
    /// <summary>按 SSE 格式拼出一个事件（event 行加 data 行，CRLF 换行），data 为 JSON 序列化后的对象。</summary>
    public static string Event(string type, object data) => "event: " + type + "\r\ndata: " + JsonSerializer.Serialize(data) + "\r\n\r\n";
    /// <summary>message_start 事件：输入 11、输出 0、缓存读取 7、缓存写入 5 个令牌，作为流式用量的起始计数。</summary>
    public static string StartEvent => Event("message_start", new { type = "message_start", message = new { usage = new { input_tokens = 11, output_tokens = 0, cache_read_input_tokens = 7, cache_creation_input_tokens = 5 } } });
    /// <summary>message_delta 事件：携带累计输出令牌数和可选的结束原因，用于验证按累计值而非增量计数。</summary>
    public static string Delta(int output, string? reason = null) => Event("message_delta", new { type = "message_delta", usage = new { output_tokens = output }, delta = new { stop_reason = reason } });
    /// <summary>message_stop 事件，表示流正常结束。</summary>
    public static string StopEvent => Event("message_stop", new { type = "message_stop" });
}
