using System.Net.Http.Json;
using HouseholdRelay;
using static Tests.TestSuite;
using static Tests.UsageFixture;

namespace Tests;

static class UsageRestartTests
{
    /// <summary>
    /// 验证正常停机会先排空在途请求并写完记录：请求在途时开始停机，请求仍返回 200，记录为成功且完整；
    /// 用同一数据目录重新启动后，个人查询看到的记录与停机前完全一致，没有被恢复流程改成中断。
    /// </summary>
    public static async Task GracefulRestart()
    {
        await using var upstream = await UpstreamFixture.Create(); await using var web = await WebFixture.Create();
        var user = web.Identities.Create("正常重启"); await upstream.Activate(web.Upstreams);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        upstream.Handler = async context => { entered.TrySetResult(); await release.Task; await Json(context, new { input_tokens = 12, output_tokens = 9 }); };
        var call = Call(web, user.Token); await entered.Task;
        var stopping = web.App.StopAsync(); release.TrySetResult();
        Check((int)await call == 200); await stopping;
        var original = web.Relay.Usage!.Query(new()).Rows.Single();
        Check(original.Outcome == "succeeded" && original.Completeness == "complete");
        await using var app = ManagementWeb.Build(web.Home.Path, 0); await app.StartAsync();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, app.Urls.Single() + "/my-usage/data"); request.Headers.Authorization = new("Bearer", user.Token);
            using var response = await web.Http.SendAsync(request); Check(response.IsSuccessStatusCode);
            var report = (await response.Content.ReadFromJsonAsync<UsageReport>())!;
            Check(report.Total == 1 && report.Rows.Single() == original, "Graceful shutdown must finish persistence before recovery runs");
        }
        // 无论断言成败都先停止重启后的主机，再由 await using 释放，避免端口和数据库被占用。
        finally { await app.StopAsync(); }
    }
}
