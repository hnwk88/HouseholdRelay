using System.Net;
using System.Text;
using System.Text.Json;
using Application.Interfaces.Configuration;
using Infrastructure.ExternalSources.ClaudeCode;
using Microsoft.AspNetCore.Http;
using static Tests.TestSuite;
using static Tests.UsageFixture;

namespace Tests;

/// <summary>真实回环 HTTP/SSE、假官方CLI与独立临时站点，验证维护覆盖完整响应且保留个人令牌与归属。</summary>
static class SiteIdentityFlowTests
{
    /// <summary>旧 SSE 未结束不提交，新官方请求全部拒绝；独立 API 同时成功，四个个人令牌维护后继续各自授权计量。</summary>
    public static async Task SseAndFourClients()
    {
        using var claude = new FakeClaudeLauncher("success");
        await using var official = await UpstreamFixture.Create(); await using var api = await UpstreamFixture.Create();
        await using var web = await WebFixture.Create(config => { config.MaxConcurrent = 8; config.MaxPerClient = 2; }, claude.Register);
        var officialId = await Official(web, official); var apiId = await api.Activate(web.Upstreams);
        web.Upstreams.Activate(officialId, web.Upstreams.ReadOverview().Version); web.Upstreams.SetFailover(false);
        var service = web.App.Services.GetRequiredService<ISiteIdentityService>(); var before = service.Current;
        var people = Enumerable.Range(0, 4).Select(index => web.Identities.Create("独立地点" + index)).ToArray();
        var identities = IdentityRows(web.Database);
        var credentialPath = Path.Combine(web.Home.Path, ".state", "official-accounts", officialId, OfficialCredential.FileName);
        var credentials = File.ReadAllBytes(credentialPath);
        official.Handler = context => Json(context, new { input_tokens = 10, output_tokens = 11, cache_read_input_tokens = 2, cache_creation_input_tokens = 3 });
        for (var index = 0; index < people.Length; index++)
        {
            using var request = Message(web, people[index].Token, index, "before"); using var response = await web.Http.SendAsync(request);
            Check(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        }
        var earlier = official.Calls.TakeLast(4).ToArray();
        Check(earlier.All(call => Device(call.Body) == before.DeviceId));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        official.Handler = async context =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync(StartEvent); await context.Response.Body.FlushAsync(); started.TrySetResult();
            await release.Task.WaitAsync(context.RequestAborted);
            await context.Response.WriteAsync(Delta(6, "end_turn") + StopEvent); await context.Response.Body.FlushAsync();
        };
        try
        {
            using var streamRequest = Message(web, people[0].Token, 0, "stream");
            using var streamResponse = await web.Http.SendAsync(streamRequest, HttpCompletionOption.ResponseHeadersRead);
            Check(streamResponse.StatusCode == HttpStatusCode.OK); await started.Task;
            using var stream = await streamResponse.Content.ReadAsStreamAsync(); var fragment = new byte[512];
            Check(await stream.ReadAsync(fragment) > 0, "The first SSE bytes reach the downstream before maintenance starts");
            var changing = service.Refresh(Guid.NewGuid().ToString("N"), before.Version, default);
            Check(!changing.IsCompleted && !service.Available && service.Current == before);
            var officialCalls = official.Calls.Count;
            for (var index = 0; index < 8; index++)
            {
                using var request = Message(web, people[1].Token, 1, "closed"); using var response = await web.Http.SendAsync(request);
                Check(response.StatusCode == HttpStatusCode.ServiceUnavailable);
            }
            Check(official.Calls.Count == officialCalls && !changing.IsCompleted && web.Settings.Read().Value.DeviceId == before.DeviceId);
            web.Upstreams.Activate(apiId, web.Upstreams.ReadOverview().Version); api.Handler = context => Json(context, new { input_tokens = 20, output_tokens = 21, cache_read_input_tokens = 0, cache_creation_input_tokens = 0 });
            using (var request = Message(web, people[2].Token, 2, "api-during"))
            using (var response = await web.Http.SendAsync(request)) Check(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            Check(!changing.IsCompleted && official.Calls.Count == officialCalls, "Independent API traffic cannot release or bypass the official SSE permit");
            release.TrySetResult(); await stream.CopyToAsync(Stream.Null); var changed = await changing.WaitAsync(TimeSpan.FromSeconds(10));
            Check(!changed.Pending && changed.Version == before.Version + 1 && service.Current.DeviceId != before.DeviceId);
            Check(File.ReadAllBytes(credentialPath).SequenceEqual(credentials), "Identity preparation never rewrites the official token file");
        }
        // 任一断言失败也先释放本用例的模拟 SSE，避免宿主清理被测试自己挂住。
        finally { release.TrySetResult(); }
        official.Handler = context => Json(context, new { input_tokens = 10, output_tokens = 11, cache_read_input_tokens = 2, cache_creation_input_tokens = 3 });
        web.Upstreams.Activate(officialId, web.Upstreams.ReadOverview().Version);
        for (var index = 0; index < people.Length; index++)
        {
            using var request = Message(web, people[index].Token, index, "after"); using var response = await web.Http.SendAsync(request);
            Check(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        }
        var later = official.Calls.TakeLast(4).ToArray();
        Check(later.All(call => Device(call.Body) == service.Current.DeviceId) && IdentityRows(web.Database) == identities);
        Check(earlier.Select(call => Session(call.Body)).SequenceEqual(later.Select(call => Session(call.Body))), "Four independent sessions retain their own identifiers");
        await Settled(web, 18);
        foreach (var person in people)
        {
            var report = await Personal(web, person.Token);
            Check(report.Rows.All(row => row.UserId == person.UserId) && report.Rows.Count(row => row.Outcome == "succeeded") >= 2);
            Check(report.Rows.Where(row => row.Outcome == "succeeded" && row.Tokens.Input == 10).All(row => row.Tokens.Output == 11));
            Check(web.Identities.Clients.Authenticate(person.Token)?.Id == person.UserId);
        }
    }

    /// <summary>维护中状态读取、备用遍历和 API 请求不启动过期官方账户续期，准备完成也不自行试发。</summary>
    public static async Task ReadPathsDoNotRefresh()
    {
        using var claude = new FakeClaudeLauncher("success");
        await using var official = await UpstreamFixture.Create(); await using var api = await UpstreamFixture.Create();
        await using var web = await WebFixture.Create(overrides: claude.Register);
        var officialId = await Official(web, official); await api.Activate(web.Upstreams);
        var person = web.Identities.Create("状态与备用检查"); var service = web.App.Services.GetRequiredService<ISiteIdentityService>();
        var directory = Path.Combine(web.Home.Path, ".state", "official-accounts", officialId);
        FakeClaude.Write(directory, "fixture-expired-for-maintenance", -1);
        var before = File.ReadAllBytes(Path.Combine(directory, OfficialCredential.FileName));
        var log = File.ReadAllText(Path.Combine(directory, "fake-calls.log")); var calls = official.Calls.Count;
        using var permit = service.Enter(); var changing = service.Refresh(Guid.NewGuid().ToString("N"), service.Current.Version, default);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, web.Url + "/status"); request.Headers.Authorization = new("Bearer", person.Token);
            using var response = await web.Http.SendAsync(request); Check(response.StatusCode == HttpStatusCode.OK);
            Check(web.Upstreams.ReadStandby().All(item => !item.Options.OfficialAccount || item.BlockedByIdentity && item.Credential is null));
            Check(await Call(web, person.Token) == HttpStatusCode.OK);
            Check(File.ReadAllText(Path.Combine(directory, "fake-calls.log")) == log && official.Calls.Count == calls);
            Check(File.ReadAllBytes(Path.Combine(directory, OfficialCredential.FileName)).SequenceEqual(before));
        }
        // 必须归还本用例的在途许可；维护结束时仍不得启动测试或续期来验证新编号。
        finally { permit.Dispose(); }
        await changing.WaitAsync(TimeSpan.FromSeconds(10));
        Check(File.ReadAllText(Path.Combine(directory, "fake-calls.log")) == log && official.Calls.Count == calls);
        Check(File.ReadAllBytes(Path.Combine(directory, OfficialCredential.FileName)).SequenceEqual(before));
    }

    /// <summary>仅假凭据与回环地址，导入、测试并激活一个真实流程中的官方账户候选。</summary>
    private static async Task<string> Official(WebFixture web, UpstreamFixture mock)
    {
        var id = await web.Upstreams.ImportOfficial(web.Upstreams.ReadActive().Version,
            new UpstreamOptions { BaseUrl = mock.Url, AllowPrivateNetwork = true, Model = "fixture-model" },
            FakeClaude.Credential("fixture-maintenance-official", 60), "维护官方夹具", default);
        var tested = await web.Upstreams.TestCandidate(id, true, default); Check(tested.CanActivate, tested.Message);
        web.Upstreams.Activate(id); return id;
    }

    /// <summary>四台客户端各自保持会话，设备值刻意不同以验证官方出口统一。</summary>
    private static HttpRequestMessage Message(WebFixture web, string token, int client, string phase)
    {
        var userId = JsonSerializer.Serialize(new { device_id = new string((char)('a' + client), 64), account_uuid = "", session_id = "independent-session-" + client });
        var request = new HttpRequestMessage(HttpMethod.Post, web.Url + "/v1/messages?case=" + phase)
        { Content = new StringContent(JsonSerializer.Serialize(new { model = "fixture-model", max_tokens = 16, stream = phase == "stream",
            messages = new[] { new { role = "user", content = "fixture-maintenance-message" } }, metadata = new { user_id = userId } }), Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new("Bearer", token); return request;
    }

    /// <summary>只在测试内提取模拟线路的设备字段。</summary>
    private static string Device(string body) => Metadata(body, "device_id");
    /// <summary>只在测试内核对会话字段保持。</summary>
    private static string Session(string body) => Metadata(body, "session_id");
    /// <summary>解析测试已知的内嵌 metadata 格式，不记录正文。</summary>
    private static string Metadata(string body, string field)
    {
        using var outer = JsonDocument.Parse(body); using var inner = JsonDocument.Parse(outer.RootElement.GetProperty("metadata").GetProperty("user_id").GetString()!);
        return inner.RootElement.GetProperty(field).GetString()!;
    }

    /// <summary>完整比较四人身份与所有令牌字段，证明维护没有重发、撤销、改版本或合并归属。</summary>
    private static string IdentityRows(RelayDatabase database)
    {
        using var connection = database.Open(); using var command = RelayDatabase.Command(connection, null,
            "SELECT u.id,u.name,u.display_name,u.enabled,t.id,t.version,t.hash,coalesce(t.revoked_at,'') FROM users u JOIN tokens t ON t.user_id=u.id ORDER BY u.id,t.version");
        using var reader = command.ExecuteReader(); var rows = new List<string>();
        while (reader.Read()) rows.Add(string.Join('|', Enumerable.Range(0, 8).Select(reader.GetValue)));
        return string.Join('\n', rows);
    }
}
