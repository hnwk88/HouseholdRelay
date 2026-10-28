using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HouseholdRelay;
using Microsoft.AspNetCore.Http;
using static Tests;

/// <summary>网络请求只带虚构个人令牌；断言读取已结束的数据库记录。</summary>
static class UsageFixture
{
    public static HttpRequestMessage Request(WebFixture web, string token, string scenario = "a", string route = "/v1/messages", string? body = null)
    {
        var request = new HttpRequestMessage(route == "/v1/models" ? HttpMethod.Get : HttpMethod.Post, web.Url + route + "?case=" + scenario);
        request.Headers.Authorization = new("Bearer", token);
        if (request.Method == HttpMethod.Post) request.Content = new StringContent(body ?? """
            {"model":"fixture-model","messages":[{"role":"user","content":"fixture-private-prompt"}],"metadata":{"user_id":"forged-other-owner"}}
            """, Encoding.UTF8, "application/json");
        return request;
    }
    public static async Task<HttpStatusCode> Call(WebFixture web, string token, string scenario = "a", string route = "/v1/messages")
    {
        using var request = Request(web, token, scenario, route);
        using var response = await web.Http.SendAsync(request);
        return response.StatusCode;
    }
    public static async Task<UsageReport> Settled(WebFixture web, long count)
    {
        await Until(() => { var report = web.Relay.Usage!.Query(new()); return report.Total == count && report.Summary.All(row => row.Pending == 0); });
        return web.Relay.Usage!.Query(new());
    }
    public static async Task<UsageReport> Personal(WebFixture web, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, web.Url + "/my-usage/data"); request.Headers.Authorization = new("Bearer", token);
        using var response = await web.Http.SendAsync(request); Check(response.IsSuccessStatusCode);
        return (await response.Content.ReadFromJsonAsync<UsageReport>())!;
    }
    public static Task Json(HttpContext context, object? usage) => context.Response.WriteAsJsonAsync(new
    { type = "message", role = "assistant", model = "fixture-model", content = new[] { new { type = "text", text = "fixture-private-answer" } }, stop_reason = "end_turn", usage });
    public static string Event(string type, object data) => "event: " + type + "\r\ndata: " + JsonSerializer.Serialize(data) + "\r\n\r\n";
    public static string StartEvent => Event("message_start", new { type = "message_start", message = new { usage = new { input_tokens = 11, output_tokens = 0, cache_read_input_tokens = 7, cache_creation_input_tokens = 5 } } });
    public static string Delta(int output, string? reason = null) => Event("message_delta", new { type = "message_delta", usage = new { output_tokens = output }, delta = new { stop_reason = reason } });
    public static string StopEvent => Event("message_stop", new { type = "message_stop" });
}
