using System.Net;
using System.Text;
using System.Text.Json;
using Core.Upstream;
using static Tests.TestSuite;

namespace Tests;

/// <summary>「登录官方账户」：用假 Claude Code 验证登录、隔离、凭据刷新后转发用新令牌，以及失败时读出原因且不留痕迹。</summary>
static class OfficialLoginTests
{
    /// <summary>
    /// 网页按「登录官方账户」（名称留空）：宿主带着 <c>CLAUDE_CODE_OAUTH_TOKEN</c>、<c>ANTHROPIC_API_KEY</c> 也不传给子进程，配置目录是数据目录下以上游主键命名的那个；
    /// 名称默认为登录邮箱，模型目录用登录令牌读取；库文件里没有令牌，页面不回显令牌。测试、切换后转发用登录令牌；
    /// 旧令牌被拒（认证暂停）后 CLI 写入新令牌，下一次转发换新连接并用新令牌；令牌已过期时中转让 CLI 刷新后再转发。
    /// </summary>
    public static async Task LoginRefreshAndForwarding()
    {
        using var claude = new FakeClaudeLauncher("success");
        await using var mock = await UpstreamFixture.Create();
        string[] inherited = ["CLAUDE_CODE_OAUTH_TOKEN", "ANTHROPIC_API_KEY"];
        foreach (var key in inherited) Environment.SetEnvironmentVariable(key, "fixture-inherited-secret");
        try
        {
            await using var web = await WebFixture.Create(overrides: claude.Register); await web.Login();
            var values = new Dictionary<string, string>
            {
                ["Login.Name"] = "", ["Login.BaseUrl"] = mock.Url, ["Login.AllowPrivateNetwork"] = "true",
                ["Version"] = web.Upstreams.ReadActive().Version.ToString(), ["__RequestVerificationToken"] = await web.Csrf("/Upstream")
            };
            using var page = await web.Post("/Upstream?handler=LoginAccount", values);
            var raw = await page.Content.ReadAsStringAsync(); var html = WebUtility.HtmlDecode(raw);
            Check(page.IsSuccessStatusCode && html.Contains($"官方账户登录成功，已保存为「{FakeClaude.Email}」") && html.Contains("<option value=\"fixture-model\""), html);
            Check(!raw.Contains("fixture-login-token") && !raw.Contains("fixture-inherited-secret"));
            var candidate = web.Upstreams.LatestCandidate()!;
            Check(candidate.Name == FakeClaude.Email && candidate.Options is { OfficialLogin: true, Mode: "oauth", Authentication: "bearer" });
            var directory = Path.Combine(web.Home.Path, ".state", "official-accounts", candidate.Id);
            using (var environment = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "fake-env.json"))))
            {
                Check(environment.RootElement.GetProperty("inherited").GetArrayLength() == 0, environment.RootElement.ToString());
                Check(environment.RootElement.GetProperty("directory").GetString() == directory && environment.RootElement.GetProperty("noProxy").GetString()!.Contains("127.0.0.1"));
            }
            Check(mock.Calls.Single(call => call.Path.Contains("/models")).Headers["Authorization"] == "Bearer " + FakeClaude.FirstToken);
            foreach (var file in Directory.GetFiles(Path.Combine(web.Home.Path, ".state"), "relay.db*"))
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); using var copy = new MemoryStream(); stream.CopyTo(copy);
                Check(!Encoding.ASCII.GetString(copy.ToArray()).Contains("fixture-login-token"), "Login tokens must stay out of the database: " + file);
            }

            web.Upstreams.ChooseModel(candidate.Id, "fixture-model");
            var report = await web.Upstreams.TestCandidate(candidate.Id, true, CancellationToken.None);
            Check(report.CanActivate, report.Message); web.Upstreams.Activate(candidate.Id);
            var person = web.Identities.Create("登录测试");
            using (var first = await UpstreamFixture.RelayRequest(web, person.Token)) Check(first.StatusCode == HttpStatusCode.OK);
            Check(mock.Calls.Last().Headers["Authorization"] == "Bearer " + FakeClaude.FirstToken);

            mock.Handler = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
            using (var rejected = await UpstreamFixture.RelayRequest(web, person.Token)) Check(rejected.StatusCode == HttpStatusCode.Unauthorized);
            using (var paused = await UpstreamFixture.RelayRequest(web, person.Token)) Check(paused.StatusCode == HttpStatusCode.ServiceUnavailable);
            mock.Handler = null; FakeClaude.Write(directory, "fixture-login-token-rotated", 60);
            using (var rotated = await UpstreamFixture.RelayRequest(web, person.Token)) Check(rotated.StatusCode == HttpStatusCode.OK);
            Check(mock.Calls.Last().Headers["Authorization"] == "Bearer fixture-login-token-rotated");

            FakeClaude.Write(directory, "fixture-login-token-expired", -1);
            using (var refreshed = await UpstreamFixture.RelayRequest(web, person.Token)) Check(refreshed.StatusCode == HttpStatusCode.OK);
            Check(mock.Calls.Last().Headers["Authorization"] == "Bearer " + FakeClaude.RefreshedToken);
            Check(File.ReadAllText(Path.Combine(directory, "fake-calls.log")).Contains("-p /usage --output-format json --no-session-persistence"));

            using var listed = await web.Http.GetAsync(web.Url + "/Upstream");
            var listing = await listed.Content.ReadAsStringAsync();
            Check(!listing.Contains("fixture-login-token") && WebUtility.HtmlDecode(listing).Contains("官方账户登录") && WebUtility.HtmlDecode(listing).Contains("自动刷新"));
        }
        // 无论成败都撤掉为本用例设的宿主变量，免得影响后面的用例。
        finally { foreach (var key in inherited) Environment.SetEnvironmentVariable(key, null); }
    }

    /// <summary>
    /// 登录在浏览器里被取消（退出码非零）、结束后没有凭据、本机没装 Claude Code：网页各读出原因与下一步，不新增上游、不留账户目录；
    /// 名称不合规则时在登录前就拒绝；贴令牌的入口不接受官方登录标记；删除登录得到的上游时连同它的账户目录一起删掉。
    /// </summary>
    public static async Task LoginFailures()
    {
        await using var mock = await UpstreamFixture.Create();
        foreach (var (scenario, expected) in new[] { ("cancel", "登录没有完成（Claude Code 登录以退出码 1 结束）"), ("no-credential", "没有拿到官方账户凭据"), ("missing", "本机没有找到 Claude Code") })
        {
            using var claude = new FakeClaudeLauncher(scenario);
            await using var web = await WebFixture.Create(overrides: claude.Register); await web.Login();
            var values = new Dictionary<string, string>
            {
                ["Login.Name"] = "失败测试", ["Login.BaseUrl"] = mock.Url, ["Login.AllowPrivateNetwork"] = "true",
                ["Version"] = web.Upstreams.ReadActive().Version.ToString(), ["__RequestVerificationToken"] = await web.Csrf("/Upstream")
            };
            using var page = await web.Post("/Upstream?handler=LoginAccount", values);
            var html = WebUtility.HtmlDecode(await page.Content.ReadAsStringAsync());
            Check(page.IsSuccessStatusCode && html.Contains(expected) && html.Contains("登录官方账户"), scenario + ": " + html);
            Check(web.Upstreams.ListCandidates().Count == 0 && NoAccountDirectories(web), scenario);
        }

        using var named = new FakeClaudeLauncher("success");
        await using var host = await WebFixture.Create(overrides: named.Register);
        var service = host.Upstreams; var options = new UpstreamOptions { BaseUrl = mock.Url, AllowPrivateNetwork = true };
        try { await service.LoginOfficial(service.ReadActive().Version, options, new string('名', 41), CancellationToken.None); Check(false, "Overlong name must be refused"); }
        // 预期的拒绝：名称超长在登录前就读出要求。
        catch (InvalidOperationException error) { Check(error.Message.Contains("供应商名称"), error.Message); }
        Check(NoAccountDirectories(host) && service.ListCandidates().Count == 0);
        try { service.SaveCandidate(service.ReadActive().Version, options with { Mode = "oauth", Authentication = "bearer", OfficialLogin = true }, "fixture-pasted-token", "贴令牌"); Check(false, "Pasted tokens must not claim a login"); }
        // 预期的拒绝：官方登录只能经登录入口建立。
        catch (InvalidOperationException error) { Check(error.Message.Contains("登录官方账户"), error.Message); }

        var removed = await service.LoginOfficial(service.ReadActive().Version, options, "将删除的账户", CancellationToken.None);
        Check(Directory.Exists(Path.Combine(host.Home.Path, ".state", "official-accounts", removed)));
        service.Delete(removed);
        Check(NoAccountDirectories(host) && service.ListCandidates().Count == 0, "Deleting an official login upstream must remove its account directory");
    }

    /// <summary>
    /// 官方登录令牌已过期、刷新又卡住时，读活动上游要等（最多 30 秒）；这段等待在连接池的锁外，其他请求不排在它后面。
    /// 慢的那次读完时拿到的是旧凭据，它开始得比已换上的新连接早，不能把新连接换回去。
    /// </summary>
    public static async Task SlowTokenReadOutsidePoolLock()
    {
        using var gate = new ManualResetEventSlim(); using var waiting = new ManualResetEventSlim();
        var source = new GatedSource(gate, waiting);
        using var pool = new UpstreamPool(source, new RelayConfiguration(), new Infrastructure.ExternalSources.Network.SystemUpstreamProxy(() => new RelayConfiguration()));
        // 两次取租约放到线程池上并行：第一次卡在读快照里，第二次若还排在锁后面就会超时，正好量出等待是否在锁外。
        var slow = Task.Run(pool.Acquire);
        Check(waiting.Wait(TimeSpan.FromSeconds(5)), "The slow read never started");
        var fast = Task.Run(pool.Acquire);
        // 第二次与 2 秒计时赛跑：先完成说明没在等锁；计时先到即判失败，卡住的任务由后面 gate.Set() 放行收场。
        Check(await Task.WhenAny(fast, Task.Delay(TimeSpan.FromSeconds(2))) == fast, "A second request must not wait behind a slow token read");
        using var current = await fast; Check(current.Snapshot.Credential == "fixture-pool-new");
        gate.Set();
        using var late = await slow; Check(late.Snapshot.Credential == "fixture-pool-new", "A read that started earlier must not replace the newer connection");
        using var next = pool.Acquire(); Check(next.Snapshot.Credential == "fixture-pool-new");
    }

    /// <summary>第一次读卡在 <paramref name="gate"/> 上并返回旧凭据（模拟等刷新），之后每次立即返回新凭据。</summary>
    private sealed class GatedSource(ManualResetEventSlim gate, ManualResetEventSlim waiting) : IActiveUpstreamSource
    {
        private int reads;
        /// <summary>按读的先后返回旧或新凭据的同一版本快照。</summary>
        public UpstreamSnapshot ReadActive()
        {
            var options = new UpstreamOptions { BaseUrl = "http://127.0.0.1:9", AllowPrivateNetwork = true };
            if (Interlocked.Increment(ref reads) > 1) return new(1, options, "fixture-pool-new");
            waiting.Set(); gate.Wait(TimeSpan.FromSeconds(10));
            return new(1, options, "fixture-pool-old");
        }
    }

    /// <summary>数据目录下没有任何官方账户目录（目录本身不存在或为空）。</summary>
    private static bool NoAccountDirectories(WebFixture web)
    {
        var root = Path.Combine(web.Home.Path, ".state", "official-accounts");
        return !Directory.Exists(root) || Directory.GetDirectories(root).Length == 0;
    }
}
