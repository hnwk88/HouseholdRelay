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

    /// <summary>兼容账户（oauth、非官方登录）在管理员换号后不重启，出站设备值就是新编号，不再是启动时配置里的旧值。</summary>
    public static async Task CompatibleOauthFollowsRefresh()
    {
        using var claude = new FakeClaudeLauncher("success");
        await using var compatible = await UpstreamFixture.Create();
        await using var web = await WebFixture.Create(overrides: claude.Register);
        await compatible.Activate(web.Upstreams, options: compatible.Options(mode: "oauth", authentication: "bearer"));
        var service = web.App.Services.GetRequiredService<ISiteIdentityService>(); var before = service.Current;
        var person = web.Identities.Create("兼容账户换号");
        using (var response = await UpstreamFixture.RelayRequest(web, person.Token, "before-refresh"))
            Check(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        Check(LegacyDevice(compatible.Calls.Last().Body) == before.DeviceId, "Compatible OAuth starts on the site device");
        var changed = await service.Refresh(Guid.NewGuid().ToString("N"), before.Version, default);
        Check(!changed.Pending && service.Current.DeviceId != before.DeviceId);
        using (var response = await UpstreamFixture.RelayRequest(web, person.Token, "after-refresh"))
            Check(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var sent = LegacyDevice(compatible.Calls.Last().Body);
        Check(sent == service.Current.DeviceId && sent != before.DeviceId, "Compatible OAuth must follow the refreshed site device without a restart");
        Console.WriteLine($"compatible_before_matches=true; compatible_after_matches_new={sent == service.Current.DeviceId}; old_after=0");
    }

    /// <summary>
    /// 私有配置被别的程序独占：缓存未建立时这次官方请求归入本地身份阻断（503 固定读句、官方收到 0 次、可换独立 API、不进暂停冷却、不记 interrupted）；
    /// 核对过一次后同一代身份不再打开该文件，独占期间 8 个官方请求照常成功。
    /// </summary>
    public static async Task OccupiedConfigurationIsLocal()
    {
        using var claude = new FakeClaudeLauncher("success");
        await using var official = await UpstreamFixture.Create(); await using var api = await UpstreamFixture.Create();
        await using var web = await WebFixture.Create(overrides: claude.Register);
        var officialId = await Official(web, official); await api.Activate(web.Upstreams);
        web.Upstreams.Activate(officialId, web.Upstreams.ReadOverview().Version); web.Upstreams.SetFailover(false);
        var preparation = web.App.Services.GetRequiredService<IClaudeConfigurationPreparation>();
        var person = web.Identities.Create("占用核对");
        var path = Path.Combine(web.Home.Path, ".state", "official-accounts", officialId, ".claude.json");
        // 受控准备让本代核对失效，下一次官方请求一定要打开文件。
        preparation.PrepareAccount(officialId);
        var calls = official.Calls.Count; string blocked;
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            using (var response = await UpstreamFixture.RelayRequest(web, person.Token, "occupied-cold"))
            {
                blocked = await response.Content.ReadAsStringAsync();
                Check(response.StatusCode == HttpStatusCode.ServiceUnavailable && blocked.Contains("设备身份暂不可用", StringComparison.Ordinal), blocked);
            }
            Check(official.Calls.Count == calls, "An occupied private configuration must not reach the official upstream");
            web.Upstreams.SetFailover(true);
            var apiCalls = api.Calls.Count;
            using (var response = await UpstreamFixture.RelayRequest(web, person.Token, "occupied-failover"))
                Check(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            Check(api.Calls.Count == apiCalls + 1 && official.Calls.Count == calls, "The independent API may serve while the official account is locally blocked");
            web.Upstreams.SetFailover(false);
        }
        // 释放后立即可用：本地阻断没有进入认证暂停或冷却。
        using (var response = await UpstreamFixture.RelayRequest(web, person.Token, "released"))
            Check(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        Check(official.Calls.Count == calls + 1);
        var warm = 0;
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            for (var index = 0; index < 8; index++)
            {
                using var response = await UpstreamFixture.RelayRequest(web, person.Token, "cached-" + index);
                if (response.StatusCode == HttpStatusCode.OK) warm++;
            }
        Check(warm == 8 && official.Calls.Count == calls + 9, "A validated account must not reopen its private configuration on every request");
        var report = await Settled(web, web.Relay.Usage!.Query(new()).Total);
        Check(report.Rows.All(row => row.Outcome != "interrupted"), "A local configuration block is not an interrupted upstream response");
        Console.WriteLine($"cold_occupied_status=503; official_calls_while_cold_occupied=0; failover_api_calls=1; warm_occupied_ok={warm}/8; claude_json_opens_per_warm_request=0");
    }

    /// <summary>
    /// 不提权、不用真客户端：假 Claude 回显它启动时在探测与抓身份目录里读到的 userID，必须等于站点编号，也等于官方模拟线路上的设备值；换号后两者一起换。
    /// </summary>
    public static async Task ProbePreseedMatchesWire()
    {
        using var claude = new FakeClaudeLauncher("success");
        await using var official = await UpstreamFixture.Create();
        await using var web = await WebFixture.Create(overrides: claude.Register);
        var service = web.App.Services.GetRequiredService<ISiteIdentityService>();
        var officialId = await Official(web, official);
        // 账户测试的探测目录先单独核对：不依赖后面业务请求的副作用，撤掉预置时这里直接报出。
        var probed = claude.SeenUserIds;
        Check(probed.Count >= 1 && probed.All(item => item.UserId == service.Current.DeviceId), "The account-test probe directory must be preseeded with the site device before the client starts");
        var person = web.Identities.Create("预置编号核对");
        using (var response = await UpstreamFixture.RelayRequest(web, person.Token, "preseed-business"))
            Check(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        // 终端入口的客户端让中转在伪控制台里抓 cli 身份，那个目录同样要预置。
        using (var request = UsageFixture.Request(web, person.Token, "preseed-cli", body: JsonSerializer.Serialize(new { model = "fixture-model", max_tokens = 16,
            messages = new[] { new { role = "user", content = "fixture-preseed-cli" } }, metadata = new { user_id = "user_" + new string('c', 64) + "_account__session_preseed-cli" } })))
        {
            request.Headers.TryAddWithoutValidation("User-Agent", $"claude-cli/{claude.Version} (external, cli)");
            using var response = await web.Http.SendAsync(request); Check(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        }
        var before = service.Current.DeviceId; var seen = claude.SeenUserIds;
        Check(seen.Count >= 3 && seen.Any(item => item.Entrypoint == "cli") && seen.All(item => item.UserId == before), "Every probe and capture directory must be preseeded with the site device: " + string.Join(',', seen.Select(item => item.Entrypoint)));
        Check(official.Calls.Where(call => call.Path.StartsWith("/v1/messages", StringComparison.Ordinal)).All(call => AnyDevice(call.Body) == before));
        var changed = await service.Refresh(Guid.NewGuid().ToString("N"), service.Current.Version, default);
        Check(!changed.Pending && service.Current.DeviceId != before);
        var earlier = seen.Count; var calls = official.Calls.Count;
        var tested = await web.Upstreams.TestCandidate(officialId, true, default); Check(tested.CanActivate, tested.Message);
        var after = claude.SeenUserIds.Skip(earlier).ToArray();
        Check(after.Length >= 1 && after.All(item => item.UserId == service.Current.DeviceId), "Probe directories follow the refreshed site device");
        Check(official.Calls.Skip(calls).Where(call => call.Path.StartsWith("/v1/messages", StringComparison.Ordinal)).All(call => AnyDevice(call.Body) == service.Current.DeviceId));
        Console.WriteLine($"preseed_samples_before={seen.Count}; preseed_samples_after={after.Length}; preseed_matches_wire=true; entrypoints={string.Join('/', seen.Concat(after).Select(item => item.Entrypoint).Distinct())}");
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
    /// <summary>只在测试内提取旧字符串格式 user_设备_account_…_session_… 的设备值。</summary>
    private static string LegacyDevice(string body)
    {
        using var outer = JsonDocument.Parse(body); var value = outer.RootElement.GetProperty("metadata").GetProperty("user_id").GetString()!;
        Check(value.StartsWith("user_", StringComparison.Ordinal) && value.Length > 69 && value[69] == '_', "Fixture body must use the legacy format");
        return value[5..69];
    }
    /// <summary>两种已支持格式都取设备值：内嵌 JSON 或旧字符串格式。</summary>
    private static string AnyDevice(string body)
    {
        using var outer = JsonDocument.Parse(body);
        return outer.RootElement.GetProperty("metadata").GetProperty("user_id").GetString()!.StartsWith('{') ? Device(body) : LegacyDevice(body);
    }
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
