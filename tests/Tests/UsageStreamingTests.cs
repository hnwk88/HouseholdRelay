using System.Net;
using System.Text;
using System.Text.Json;
using HouseholdRelay;
using Microsoft.AspNetCore.Http;
using static Tests.TestSuite;
using static Tests.UsageFixture;

namespace Tests;

static class UsageStreamingTests
{
    /// <summary>
    /// 验证 SSE 用量解析能应对逐字节分片、累计计数、错误事件和缺失结尾：上游每个字节单独发送，多字节字符原样到达客户端；
    /// 未知事件里的用量被忽略，重复的累计输出不重复计数；正常结束记为成功且完整（输出 9），无结尾记为中断（输出 4），
    /// 错误事件记为失败（输出 4），message_stop 缺最后的空行记为中断（输出 9）；各场景的输入与缓存令牌都正确。
    /// </summary>
    public static async Task FragmentsAndEndings()
    {
        await using var upstream = await UpstreamFixture.Create(); await using var web = await WebFixture.Create();
        var user = web.Identities.Create("流式"); await upstream.Activate(web.Upstreams);
        upstream.Handler = async context =>
        {
            context.Response.ContentType = "text/event-stream";
            var scenario = context.Request.Query["case"].ToString();
            var text = StartEvent + Delta(1) + "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"delta\":{\"text\":\"汉😀\"}}\n\n"
                + Event("future_event", new { type = "future_event", usage = new { input_tokens = 50000, output_tokens = 50000 } }) + Delta(4);
            text += scenario switch
            {
                "complete" => Delta(9, "end_turn") + Delta(9, "end_turn") + StopEvent,
                "error" => Event("error", new { type = "error", error = new { type = "overloaded_error", message = "fixture failure" } }),
                "tail" => Delta(9, "end_turn") + StopEvent.TrimEnd('\r', '\n'),
                _ => ""
            };
            var bytes = Encoding.UTF8.GetBytes(text);
            // 每个 UTF-8 字节都可以成为独立网络分片。
            for (var index = 0; index < bytes.Length; index++)
            { await context.Response.Body.WriteAsync(bytes.AsMemory(index, 1), context.RequestAborted); await context.Response.Body.FlushAsync(context.RequestAborted); }
        };
        var expected = new[] { ("complete", "succeeded", "complete", 9L), ("truncated", "interrupted", "partial", 4L), ("error", "failed", "partial", 4L), ("tail", "interrupted", "partial", 9L) };
        var count = 0;
        foreach (var (scenario, outcome, completeness, output) in expected)
        {
            using var request = Request(web, user.Token, scenario); using var response = await web.Http.SendAsync(request);
            Check(response.StatusCode == HttpStatusCode.OK);
            var text = await response.Content.ReadAsStringAsync();
            var data = text.Split('\n').Where(line => line.StartsWith("data:", StringComparison.Ordinal)).Select(line => line[5..].Trim()).ToArray();
            Check(data.Any(item => { using var json = JsonDocument.Parse(item); return json.RootElement.TryGetProperty("delta", out var delta) && delta.TryGetProperty("text", out var value) && value.GetString() == "汉😀"; }));
            var report = await Settled(web, ++count); var row = report.Rows[0];
            Check(row.Outcome == outcome && row.Completeness == completeness, scenario + " completion");
            Check(row.Tokens == new UsageTokens(11, output, 7, 5), scenario + " cumulative tokens");
        }
    }

    /// <summary>
    /// 验证客户端取消与上游超时都保留部分计数并释放连接：流进行中记录处于待写入，同时修改配置可在 1 秒内提交（说明没有跨网络持有写事务）；
    /// 客户端取消记为已取消，超时记为失败，二者都标为部分完整且保留输入 11、输出 4、已派发；
    /// 模拟上游必须收到中止，中转活动请求数归零。
    /// </summary>
    public static async Task CancellationAndTimeout()
    {
        foreach (var cancel in new[] { true, false })
        {
            await using var upstream = await UpstreamFixture.Create();
            await using var web = await WebFixture.Create(config => config.RequestTimeoutSeconds = cancel ? 5 : 1);
            var user = web.Identities.Create("取消"); await upstream.Activate(web.Upstreams);
            var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            upstream.Handler = async context =>
            {
                context.Response.ContentType = "text/event-stream";
                try
                {
                    await context.Response.WriteAsync(StartEvent + Delta(4), context.RequestAborted); await context.Response.Body.FlushAsync(context.RequestAborted);
                    await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
                }
                // 客户端取消或中转超时都必须中止上游请求；捕获到取消即证明取消已传播到模拟上游。
                catch (OperationCanceledException) { released.TrySetResult(); }
            };
            using var cancellation = new CancellationTokenSource(); using var request = Request(web, user.Token);
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, MaxResponseDrainSize = 0 });
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
            await using var stream = await response.Content.ReadAsStreamAsync(); var first = new byte[4096];
            var received = "";
            while (!received.Contains("\"output_tokens\":4", StringComparison.Ordinal))
            { var read = await stream.ReadAsync(first).AsTask().WaitAsync(TimeSpan.FromSeconds(2)); Check(read > 0); received += Encoding.UTF8.GetString(first, 0, read); }
            Check(web.Relay.Usage!.Query(new()).Summary[0].Pending == 1);
            // 流开放时配置提交可完成，证明没有跨网络请求持有写事务。
            await Task.Run(() => web.Identities.Rename(user.UserId, "取消后仍归本人")).WaitAsync(TimeSpan.FromSeconds(1));
            if (cancel) { cancellation.Cancel(); response.Dispose(); }
            else { try { await stream.CopyToAsync(Stream.Null); } catch (IOException) { /* 超时后中转会中断响应，读取剩余内容时出现 IO 异常属于预期，结论以用量记录为准。 */ } }
            await released.Task.WaitAsync(TimeSpan.FromSeconds(4));
            var report = await Settled(web, 1); var row = report.Rows[0];
            Check(row.Outcome == (cancel ? "cancelled" : "failed") && row.Completeness == "partial", $"cancel={cancel}, outcome={row.Outcome}, completeness={row.Completeness}");
            Check(row.Tokens.Input == 11 && row.Tokens.Output == 4 && row.Dispatched);
            Check(web.Relay.Admission.Active == 0 && upstream.Calls.Count == 3);
        }
    }

    /// <summary>
    /// 验证流式用量解析的内存只受单个事件大小约束：160 个各 32KB 的事件（约 5MB）能完整转发且记录为完整；
    /// 单个事件超过 1MB 且不结束时中转放弃该流，记录为失败、部分完整，并保留已解析到的输入 11。
    /// </summary>
    public static async Task BoundedEvents()
    {
        await using var upstream = await UpstreamFixture.Create(); await using var web = await WebFixture.Create();
        var user = web.Identities.Create("大流"); await upstream.Activate(web.Upstreams);
        upstream.Handler = async context =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync(StartEvent, context.RequestAborted);
            var fragment = Event("content_block_delta", new { type = "content_block_delta", delta = new { text = new string('x', 32768) } });
            for (var index = 0; index < 160; index++) await context.Response.WriteAsync(fragment, context.RequestAborted);
            await context.Response.WriteAsync(Delta(9, "end_turn") + StopEvent, context.RequestAborted);
        };
        using (var request = Request(web, user.Token))
        using (var response = await web.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead))
        { await response.Content.CopyToAsync(Stream.Null); Check(response.IsSuccessStatusCode); }
        Check((await Settled(web, 1)).Rows[0].Completeness == "complete");
        upstream.Handler = async context =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync(StartEvent, context.RequestAborted);
            await context.Response.WriteAsync("data: " + new string('x', 1024 * 1024 + 1), context.RequestAborted);
        };
        using (var request = Request(web, user.Token))
        {
            try { using var response = await web.Http.SendAsync(request); }
            // 超大事件会让中转中止响应，客户端可能收到请求异常，这是预期结果，结论以用量记录为准。
            catch (HttpRequestException) { }
        }
        var row = (await Settled(web, 2)).Rows[0];
        Check(row.Outcome == "failed" && row.Completeness == "partial" && row.Tokens.Input == 11);
    }
}
