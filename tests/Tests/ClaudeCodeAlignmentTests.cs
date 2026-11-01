using System.Net;
using System.Text;
using System.Text.Json;
using static Tests.TestSuite;

namespace Tests;

/// <summary>
/// 官方账户的请求头与正文讲同一台机器、同一个 Claude Code 版本和入口：请求头的入口跟正文计费头走；系统提示的环境段一字不改；
/// 客户端比中转新时中转在后台升级自己的 Claude Code（只跑一次、失败读出原话、不挡转发），客户端更旧时不降级只读出提示。全部用假 Claude Code，不真联网升级。
/// </summary>
static class ClaudeCodeAlignmentTests
{
    /// <summary>官方账户登录文件里的访问令牌。</summary>
    private const string Token = "fixture-alignment-official-token";

    /// <summary>
    /// 入口与版本对齐：三台客户端正文计费头分别写 cli、sdk-cli、claude-vscode（User-Agent 故意写成别的入口，正文为准），版本都与中转的假 Claude Code 相同。
    /// 发到上游的 User-Agent 入口与版本都等于正文计费头里的；cli 那份来自伪控制台里的交互模式（假 Claude Code 只有在真终端里才报 cli）；三份除入口外的身份头完全相同。
    /// </summary>
    public static async Task EntrypointFollowsBody()
    {
        using var claude = new FakeClaudeLauncher("success");
        await using var mock = await UpstreamFixture.Create();
        await using var web = await Official(claude, mock);
        var person = web.Identities.Create("书房");
        var sent = new List<UpstreamCall>();
        foreach (var (entrypoint, agent) in new[] { ("cli", "sdk-cli"), ("sdk-cli", "cli"), ("claude-vscode", "cli") })
        {
            using (var response = await Send(web, person.Token, Body(claude.Version, entrypoint), $"claude-cli/{claude.Version} (external, {agent})"))
                Check(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            sent.Add(mock.Calls.Last());
        }
        foreach (var call in sent)
        {
            var billing = ClaudeCodeClient.Read(Encoding.UTF8.GetBytes(call.Body), null); var agent = ClaudeCodeClient.Read([], call.Headers["User-Agent"]);
            Check(billing.Entrypoint is not null && agent == billing, $"User-Agent {call.Headers["User-Agent"]} must say what the body says: {billing}");
        }
        var expected = FakeClaude.Identity(claude.Version).Where(header => header.Key != "User-Agent").ToArray();
        foreach (var call in sent) foreach (var (name, value) in expected) Check(call.Headers[name] == value, $"{name}: {call.Headers[name]}");
        Check(sent[0].Headers["User-Agent"] == $"claude-cli/{claude.Version} (external, cli)", "cli must come from interactive mode in a pseudo console: " + sent[0].Headers["User-Agent"]);
    }

    /// <summary>环境段逐字不变：正文系统提示里有工作目录（带用户名）、Platform、Shell、OS Version，还有工具列表；正文不带设备编号时，发到上游的正文与客户端发来的逐字节相同。</summary>
    public static async Task EnvironmentUnchanged()
    {
        using var claude = new FakeClaudeLauncher("success");
        await using var mock = await UpstreamFixture.Create();
        await using var web = await Official(claude, mock);
        var person = web.Identities.Create("书房");
        var body = JsonSerializer.Serialize(new
        {
            model = "fixture-model", max_tokens = 16,
            system = new object[] { new { type = "text", text = $"x-anthropic-billing-header: cc_version={claude.Version}.abc; cc_entrypoint=cli;" },
                new { type = "text", text = "# Environment\n - Primary working directory: C:\\Users\\family.member\\projects\\家里的工程\n - Is a git repository: true\n - Platform: darwin\n - Shell: zsh\n - OS Version: Darwin 24.1.0" } },
            tools = new object[] { new { name = "Bash", description = "Run a command", input_schema = new { type = "object" } } },
            messages = new[] { new { role = "user", content = "hi" } }
        });
        using (var response = await Send(web, person.Token, body, null)) Check(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        Check(mock.Calls.Last().Body == body, "The environment section, tools and every other byte must reach the upstream unchanged");
    }

    /// <summary>
    /// 客户端更新时自动升级只跑一次：中转的假 Claude Code 是 1.0.0，三台客户端同时发来 1.0.1 的请求；三个请求都照常用 1.0.0 的身份发出并成功，假 claude update 只跑了一次；
    /// 升级完成后下一个请求带 1.0.1 的身份；上游页读出升级结果。
    /// </summary>
    public static async Task NewerClientUpgradesOnce()
    {
        using var claude = new FakeClaudeLauncher("success") { UpdateTo = "1.0.1" };
        await using var mock = await UpstreamFixture.Create();
        await using var web = await Official(claude, mock);
        var person = web.Identities.Create("书房");
        // 三个请求同时发：它们会同时看到「客户端更新」，用来证明升级只起一次。
        var calls = Enumerable.Range(0, 3).Select(async _ =>
        {
            using var response = await Send(web, person.Token, Body("1.0.1", "sdk-cli"), null);
            return response.StatusCode;
        }).ToArray();
        // 等三个都回来再判：升级在后台跑，请求不该等它。
        Check((await Task.WhenAll(calls)).All(status => status == HttpStatusCode.OK), "Requests must not wait for the upgrade");
        Check(mock.Calls.TakeLast(3).All(call => call.Headers["User-Agent"] == "claude-cli/1.0.0 (external, sdk-cli)"), "Requests during the upgrade keep the current identity");
        await Until(async () => claude.Version == "1.0.1" && !(await Upstream(web)).Contains("正在后台升级", StringComparison.Ordinal));
        Check(claude.Updates == 1, $"claude update must run once for concurrent requests, ran {claude.Updates}");
        using (var after = await Send(web, person.Token, Body("1.0.1", "sdk-cli"), null)) Check(after.StatusCode == HttpStatusCode.OK);
        Check(mock.Calls.Last().Headers["User-Agent"] == "claude-cli/1.0.1 (external, sdk-cli)", "After the upgrade the identity is recaptured: " + mock.Calls.Last().Headers["User-Agent"]);
        Check((await Upstream(web)).Contains("已把中转的 Claude Code 从 1.0.0 升到 1.0.1", StringComparison.Ordinal));
    }

    /// <summary>
    /// 升级失败：claude update 往标准错误写一句带代理凭据的报错并退出 1。转发照常成功；上游页读出「升级失败（退出码 1）」和去掉凭据的升级命令原话；
    /// 一小时之内再来的请求不再自动升级；按「升级中转的 Claude Code」（带防 CSRF 令牌）不受间隔限制，立即再跑一次并读出同一结果。
    /// </summary>
    public static async Task UpgradeFailureReadsOutput()
    {
        using var claude = new FakeClaudeLauncher("update-fail");
        await using var mock = await UpstreamFixture.Create();
        await using var web = await Official(claude, mock);
        var person = web.Identities.Create("书房");
        using (var first = await Send(web, person.Token, Body("2.0.0", "sdk-cli"), null)) Check(first.StatusCode == HttpStatusCode.OK, "A failing upgrade must not affect forwarding");
        await Until(async () => (await Upstream(web)).Contains("升级失败", StringComparison.Ordinal));
        var page = await Upstream(web);
        Check(page.Contains("升级失败（退出码 1），版本仍是 1.0.0。升级命令原话：npm ERR! code EBUSY fixture-update-failure via http://[redacted]@127.0.0.1:7897", StringComparison.Ordinal), page);
        Check(!page.Contains("fixture-proxy-secret", StringComparison.Ordinal), "Proxy credentials must be redacted");
        using (var second = await Send(web, person.Token, Body("2.0.0", "sdk-cli"), null)) Check(second.StatusCode == HttpStatusCode.OK);
        await Task.Delay(500);
        Check(claude.Updates == 1, "No automatic retry within the hour");
        using var pressed = await web.Post("/Upstream?handler=UpgradeClaudeCode", new() { ["__RequestVerificationToken"] = await web.Csrf("/Upstream") });
        var html = WebUtility.HtmlDecode(await pressed.Content.ReadAsStringAsync());
        Check(pressed.StatusCode == HttpStatusCode.OK && claude.Updates == 2 && html.Contains("<p class=\"notice\" role=\"status\">升级失败（退出码 1）", StringComparison.Ordinal), html);
        using (var request = new HttpRequestMessage(HttpMethod.Post, web.Url + "/Upstream?handler=UpgradeClaudeCode") { Content = new FormUrlEncodedContent([]) })
        {
            request.Headers.TryAddWithoutValidation("Origin", web.Url);
            using var refused = await web.Http.SendAsync(request); Check(refused.StatusCode == HttpStatusCode.BadRequest && claude.Updates == 2, "The button requires the antiforgery token");
        }
    }

    /// <summary>客户端更旧时不升级：客户端发来 0.9.0，中转是 1.0.0；请求成功，claude update 一次都没跑；上游页读出「这台客户端的 Claude Code 比中转的旧，请在那台电脑上升级」。</summary>
    public static async Task OlderClientNoUpgrade()
    {
        using var claude = new FakeClaudeLauncher("success") { UpdateTo = "1.0.1" };
        await using var mock = await UpstreamFixture.Create();
        await using var web = await Official(claude, mock);
        var person = web.Identities.Create("卧室");
        using (var response = await Send(web, person.Token, Body("0.9.0", "cli"), null)) Check(response.StatusCode == HttpStatusCode.OK);
        await Task.Delay(500);
        var page = await Upstream(web);
        Check(claude.Updates == 0 && page.Contains("最近一次看到的客户端是「卧室」的 Claude Code 0.9.0", StringComparison.Ordinal)
            && page.Contains("这台客户端的 Claude Code 比中转的旧，请在那台电脑上升级。", StringComparison.Ordinal), page);
        Check(page.Contains("官方能从系统提示里看到客户端的系统、命令行和工作目录", StringComparison.Ordinal), "The page must read the environment note");
    }

    /// <summary>带假 Claude Code 的网页主机里导入一个官方账户上游（指向模拟上游），测试通过并切换过去。</summary>
    private static async Task<WebFixture> Official(FakeClaudeLauncher claude, UpstreamFixture mock)
    {
        var web = await WebFixture.Create(overrides: claude.Register);
        var id = await web.Upstreams.ImportOfficial(web.Upstreams.ReadActive().Version, new UpstreamOptions { BaseUrl = mock.Url, AllowPrivateNetwork = true, Model = "fixture-model" },
            FakeClaude.Credential(Token, 60), "官方", CancellationToken.None);
        var report = await web.Upstreams.TestCandidate(id, true, CancellationToken.None);
        Check(report.CanActivate, report.Message); web.Upstreams.Activate(id);
        await web.Login(); return web;
    }

    /// <summary>客户端 Claude Code 发来的最小正文：<c>system[0]</c> 是计费头，写明版本与入口。</summary>
    private static string Body(string version, string entrypoint) => JsonSerializer.Serialize(new
    {
        model = "fixture-model", max_tokens = 16,
        system = new[] { new { type = "text", text = $"x-anthropic-billing-header: cc_version={version}.abc; cc_entrypoint={entrypoint};" }, new { type = "text", text = "You are Claude Code." } },
        messages = new[] { new { role = "user", content = "hi" } }
    });

    /// <summary>以个人令牌经中转发一条 Messages 请求；<paramref name="agent"/> 不为空时作为客户端 User-Agent。调用方释放响应。</summary>
    private static async Task<HttpResponseMessage> Send(WebFixture web, string token, string body, string? agent)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, web.Url + "/v1/messages?beta=true") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new("Bearer", token);
        if (agent is not null) request.Headers.TryAddWithoutValidation("User-Agent", agent);
        return await web.Http.SendAsync(request);
    }

    /// <summary>打开上游页（夹具已登录），返回解码 HTML 实体后的正文。</summary>
    private static async Task<string> Upstream(WebFixture web) => WebUtility.HtmlDecode(await web.Http.GetStringAsync(web.Url + "/Upstream"));

    /// <summary>每 200 毫秒查一次，最多等 30 秒；到时还不成立就判失败。</summary>
    private static async Task Until(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!await condition()) { Check(DateTime.UtcNow < deadline, "Timed out waiting for the background upgrade"); await Task.Delay(200); }
    }
}
