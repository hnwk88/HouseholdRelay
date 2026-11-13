using Application.Interfaces.Configuration;
using Application.Services.Configuration;
using static Tests.TestSuite;

namespace Tests;

/// <summary>在两次官方准入之间注入维护拒绝，验证活动与备用快照不会中断独立 API 或重置暂停状态。</summary>
public static class UpstreamIdentityRaceTests
{
    /// <summary>真实服务读取路径的第二次准入被拒仍返回带标记快照；池保留原连接，后续独立 API 仍可选。</summary>
    public static async Task BlockedReadsPreserveConnections()
    {
        using var claude = new FakeClaudeLauncher("success");
        await using var mock = await UpstreamFixture.Create();
        RaceIdentity? controlled = null;
        await using var web = await WebFixture.Create(overrides: services =>
        {
            claude.Register(services);
            services.AddSingleton<SiteIdentityService>();
            services.AddSingleton<ISiteIdentityService>(provider => controlled = new(provider.GetRequiredService<SiteIdentityService>()));
        });
        var official = await web.Upstreams.ImportOfficial(web.Upstreams.ReadActive().Version, mock.Options(), FakeClaude.Credential("fixture-race-official", 60), "模拟官方", default);
        Check((await web.Upstreams.TestCandidate(official, true, default)).CanActivate);
        web.Upstreams.Activate(official);
        var api = await AddApi(web, mock, "备用 API");
        web.Upstreams.SetFailover(true);
        using (var original = web.Relay.Upstreams.Acquire())
        {
            PauseAndCool(original);
            controlled!.RejectSecondEntry();
            using var raced = web.Relay.Upstreams.Acquire();
            Check(controlled.EntryCalls == 2, "The injected refusal must be inside ReadAccessToken after the first permit succeeds.");
            Check(ReferenceEquals(original, raced) && raced.Paused && raced.CooldownSeconds > 0 && ((UpstreamConnection)raced).IdentityReadBlocked,
                "A blocked read must retain the old active connection and close it even when availability has already recovered.");
        }
        controlled!.RejectSecondEntry();
        var blocked = web.Upstreams.ReadActive();
        Check(blocked.BlockedByIdentity && blocked.Credential is null && controlled.Available, "A blocked snapshot records the observed race, independently of current availability.");
        controlled.RejectSecondEntry();
        using (var route = web.Relay.Upstreams.Route())
        {
            route.CheckState();
            Check(route.Next("fixture-model")?.Snapshot.CandidateId == api, "Independent API fallback must survive the official credential read race.");
        }
        web.Upstreams.Activate(api);
        var anotherApi = await AddApi(web, mock, "另一个 API");
        var pool = (UpstreamPool)web.Relay.Upstreams;
        var before = pool.Standby();
        try
        {
            var originalOfficial = before.Single(connection => connection.Snapshot.CandidateId == official);
            PauseAndCool(originalOfficial);
            controlled.RejectSecondEntry();
            var after = pool.Standby();
            try
            {
                var kept = after.Single(connection => connection.Snapshot.CandidateId == official);
                Check(controlled.EntryCalls == 2 && ReferenceEquals(originalOfficial, kept) && kept.Paused && kept.CooldownSeconds > 0 && kept.IdentityReadBlocked,
                    "Blocked standby reads preserve the exact connection and its prior pause/cooldown.");
                Check(after.Any(connection => connection.Snapshot.CandidateId == anotherApi && connection.Usable), "Enumeration must continue to the API after the refused official candidate.");
            }
            // 每份备用列表都持有独立租约，失败断言也必须逐个归还。
            finally { foreach (var connection in after) connection.Dispose(); }
        }
        // 不泄漏预热取得的连接引用，否则测试会掩盖真实池释放问题。
        finally { foreach (var connection in before) connection.Dispose(); }
    }

    /// <summary>已选中官方后的准入拒绝只换独立 API；私有配置坏时不读令牌、不发官方，修复重读后原连接恢复。</summary>
    public static async Task OperationAndConfigurationRefusals()
    {
        using var claude = new FakeClaudeLauncher("success");
        await using var mock = await UpstreamFixture.Create();
        RaceIdentity? controlled = null;
        await using var web = await WebFixture.Create(overrides: services =>
        {
            claude.Register(services); services.AddSingleton<SiteIdentityService>();
            services.AddSingleton<ISiteIdentityService>(provider => controlled = new(provider.GetRequiredService<SiteIdentityService>()));
        });
        var official = await AddOfficial(web, mock, "主要官方");
        web.Upstreams.Activate(official);
        await AddOfficial(web, mock, "备用官方");
        var api = await AddApi(web, mock, "独立 API"); web.Upstreams.SetFailover(true);
        var token = web.Identities.Create("竞争客户端").Token;
        var body = DeviceMetadataPathTests.Sample(false, new string('a', 64), "fixture-model", claude.Version, "sdk-cli");
        var before = mock.Calls.Count;
        controlled!.RejectEntry(3);
        using (var request = UsageFixture.Request(web, token, "permit-window", body: body))
        using (var response = await web.Http.SendAsync(request)) Check(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        Check(mock.Calls.Count == before + 1 && mock.Calls.Last().Headers.ContainsKey("x-api-key") && !mock.Calls.Last().Headers.ContainsKey("Authorization"),
            "The post-selection refusal must skip the other official account and send only to the independent API.");
        using var original = web.Relay.Upstreams.Acquire();
        var path = Path.Combine(web.Home.Path, ".state", "official-accounts", official, ".claude.json");
        var originalBytes = File.ReadAllBytes(path);
        File.WriteAllText(path, "{broken-identity-configuration");
        // 普通转发按身份代次缓存核对结果、不再每次打开私有配置；损坏在下一次受控准备（或续期启动前重读）时发现。
        // 这里走一次受控准备：它拒绝损坏文件、不改原字节，并让本代缓存失效，下一次读取据实阻断。
        var refused = false;
        try { web.App.Services.GetRequiredService<IClaudeConfigurationPreparation>().PrepareAccount(official); }
        // 损坏配置的固定拒绝是本步预期结果。
        catch (ClaudeConfigurationUnavailableException) { refused = true; }
        Check(refused && File.ReadAllText(path) == "{broken-identity-configuration", "Controlled preparation refuses the damaged file without rewriting it.");
        using (var blocked = web.Relay.Upstreams.Acquire())
            Check(ReferenceEquals(original, blocked) && ((UpstreamConnection)blocked).IdentityReadBlocked && !blocked.Paused && blocked.CooldownSeconds == 0,
                "A known configuration refusal preserves credentials and upstream states, but closes the existing connection.");
        using (var route = web.Relay.Upstreams.Route())
        {
            route.CheckState();
            var selected = route.Next("fixture-model");
            Check(selected?.Snapshot.CandidateId != official, "The broken official account is excluded even though the site is otherwise available.");
        }
        web.Upstreams.Delete(web.Upstreams.ListCandidates().Single(candidate => candidate.Name == "备用官方").Id);
        before = mock.Calls.Count;
        using (var request = UsageFixture.Request(web, token, "damaged-configuration", body: body))
        using (var response = await web.Http.SendAsync(request)) Check(response.IsSuccessStatusCode);
        Check(mock.Calls.Count == before + 1 && mock.Calls.Last().Headers.ContainsKey("x-api-key"), "A damaged official configuration cannot block the API candidate.");
        Check(File.ReadAllText(path) == "{broken-identity-configuration", "Ordinary forwarding must not repair damaged private configuration.");
        File.WriteAllBytes(path, originalBytes);
        using var restored = web.Relay.Upstreams.Acquire();
        Check(ReferenceEquals(original, restored) && !((UpstreamConnection)restored).IdentityReadBlocked && !restored.Paused && restored.CooldownSeconds == 0,
            "A successful credential reread clears only the local block without replacing the connection.");
        Check(web.Upstreams.ReadCandidate(api).State == "passed");
    }

    /// <summary>导入并测试独立假官方账户；不改变当前活动上游。</summary>
    private static async Task<string> AddOfficial(WebFixture web, UpstreamFixture mock, string name)
    {
        var id = await web.Upstreams.ImportOfficial(web.Upstreams.ReadActive().Version, mock.Options(), FakeClaude.Credential("fixture-race-official", 60), name, default);
        Check((await web.Upstreams.TestCandidate(id, true, default)).CanActivate);
        return id;
    }

    /// <summary>创建并测试一个独立 API 候选，但不切换当前活动上游。</summary>
    private static async Task<string> AddApi(WebFixture web, UpstreamFixture mock, string name)
    {
        var id = web.Upstreams.SaveCandidate(web.Upstreams.ReadActive().Version, mock.Options(), "fixture-race-api", name);
        Check((await web.Upstreams.TestCandidate(id, true, default)).CanActivate);
        return id;
    }

    /// <summary>在现有连接上注入两类官方拒绝，后续维护读取不得清除任一状态。</summary>
    private static void PauseAndCool(IUpstreamLease connection)
    {
        using var unauthorized = new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized);
        using var limited = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
        connection.Observe(unauthorized); connection.Observe(limited);
    }

    /// <summary>只在指定的第二次准入模拟维护拒绝，其余动作委托真实服务；Available 保持真实值以覆盖瞬时状态已恢复的竞争。</summary>
    private sealed class RaceIdentity(ISiteIdentityService inner) : ISiteIdentityService
    {
        private int remaining;
        /// <summary>从本次注入设置以来发生的准入次数。</summary>
        public int EntryCalls { get; private set; }
        /// <summary>真实服务持久编号与代次。</summary>
        public SiteIdentitySnapshot Current => inner.Current;
        /// <summary>真实就绪状态，不因注入而造假。</summary>
        public bool Available => inner.Available;
        /// <summary>真实服务的管理员读句。</summary>
        public string Status => inner.Status;
        /// <summary>下一轮在账户读取的内部准入拒绝，避免依赖定时或线程调度。</summary>
        public void RejectSecondEntry() => RejectEntry(2);
        /// <summary>精确指定拒绝发生在凭据读取内部还是业务取得操作许可时。</summary>
        public void RejectEntry(int count) { remaining = count; EntryCalls = 0; }
        /// <summary>恰好第二次准入抛出明确维护异常；其他许可完全来自真实服务。</summary>
        public IDisposable Enter()
        {
            EntryCalls++;
            if (remaining > 0 && --remaining == 0) throw new SiteIdentityUnavailableException();
            return inner.Enter();
        }
        /// <summary>宿主启动按真实服务准备私有配置。</summary>
        public Task Prepare(CancellationToken cancellation) => inner.Prepare(cancellation);
        /// <summary>维护仍执行真实排空和持久提交。</summary>
        public Task<SiteIdentityOperationResult> Refresh(string operationId, long expectedVersion, CancellationToken cancellation) => inner.Refresh(operationId, expectedVersion, cancellation);
        /// <summary>恢复仍由真实服务完成。</summary>
        public Task<SiteIdentityOperationResult> Resume(string operationId, CancellationToken cancellation) => inner.Resume(operationId, cancellation);
    }
}
