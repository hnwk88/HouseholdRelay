using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using Application.Interfaces.Security;
using Application.Interfaces.Configuration;
using Application.Interfaces.ClaudeCode;
using Infrastructure.ExternalSources.ClaudeCode;
using Microsoft.AspNetCore.Http;
using static Tests.TestSuite;
using static Tests.UsageFixture;

namespace Tests;

/// <summary>
/// 官方账户只让官方看到一台设备：转发、测试、获取模型都只带中转所在电脑 Claude Code 的统一身份；身份没到手宁可不发；本机 Claude Code 升级后自动重取；
/// API 密钥上游与兼容中转发出的请求与改动前逐字相同。
/// </summary>
static class ClaudeCodeIdentityTests
{
    /// <summary>官方账户登录文件里的访问令牌；断言它不出现在读句里。</summary>
    private const string Token = "fixture-identity-official-token";
    /// <summary>仅用于两个全新隔离目录的同一固定假设备号，不来自或写入任何个人配置。</summary>
    private static readonly string _nativeDevice = new('d', 64);
    /// <summary>不算身份、跟着每次请求或连接走的头：认证、主机、长度、协议版本、beta、内容类型、编码和会话编号。</summary>
    private static readonly HashSet<string> _perRequest = new(StringComparer.OrdinalIgnoreCase)
    { "Authorization", "Host", "Content-Length", "Content-Type", "Accept-Encoding", "Connection", "anthropic-version", "anthropic-beta", "X-Claude-Code-Session-Id",
        "X-Stainless-Retry-Count", "X-Claude-Code-Agent-Id", "X-Claude-Code-Parent-Agent-Id" };

    /// <summary>
    /// 抓包对照（D-011 的自查，按用户 2026-11-01 定的「同一入口完全相同」）：先让本机真 claude.exe 用交互模式与 <c>--print</c> 各直接打一次模拟端点，记下它实际发出的请求头；
    /// 再让两台标识完全不同的终端客户端（入口 cli）和一台 SDK 宿主（入口 sdk-cli）经中转打官方账户上游。两台终端客户端发到上游的头逐项相同，且等于真 claude.exe 交互模式那一份；
    /// SDK 宿主的等于 <c>--print</c> 那一份；两份只差 User-Agent 里的入口。客户端自己的标识和没见过的头一个都不带出去；设备编号统一成中转的设备编号。
    /// 客户端自报的版本都不比本机的新，这个用例里不起后台升级。
    /// </summary>
    public static async Task AccountForwardingUsesOneIdentity()
    {
        var interactive = Identity(await Native(true)); var print = Identity(await Native(false));
        Check(interactive["User-Agent"].EndsWith("(external, cli)", StringComparison.Ordinal) && print["User-Agent"].EndsWith("(external, sdk-cli)", StringComparison.Ordinal),
            interactive["User-Agent"] + " / " + print["User-Agent"]);
        Check(interactive.Keys.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(print.Keys.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)
            && interactive.All(header => header.Key == "User-Agent" || print[header.Key] == header.Value), "The two modes of real claude.exe differ only in the User-Agent entrypoint");
        var identity = interactive;
        Check(identity.ContainsKey("User-Agent") && identity.ContainsKey("x-app") && identity.Keys.Any(name => name.StartsWith("x-stainless-", StringComparison.OrdinalIgnoreCase)),
            "Real claude.exe must send its identity: " + string.Join(", ", identity.Keys));
        // 本机这一版真 claude.exe 的每个身份头都已逐项分类；出现白名单外的头说明 Claude Code 加了新头，要先分类再发。
        Check(identity.Keys.Concat(print.Keys).All(ClaudeCodeIdentity.StableHeaders.Contains), "Real claude.exe sent an unclassified header: "
            + string.Join(", ", identity.Keys.Concat(print.Keys).Where(name => !ClaudeCodeIdentity.StableHeaders.Contains(name)).Distinct(StringComparer.OrdinalIgnoreCase)));
        using var claude = new FakeClaudeLauncher("success");
        await using var mock = await UpstreamFixture.Create();
        await using var web = await Official(claude, mock, services => services.AddSingleton<IClaudeCodeIdentitySource>(provider =>
        {
            var launcher = new NativeCliIsolationLauncher();
            return new ClaudeCodeIdentitySource(launcher, new ClaudeCodeProbe(launcher, provider.GetRequiredService<ISecretProtector>(), provider.GetRequiredService<IUpstreamProxy>(), provider.GetRequiredService<IClaudeConfigurationPreparation>(), provider.GetRequiredService<ISiteIdentityService>(), provider.GetRequiredService<IOwnedClaudeProcesses>(), provider.GetRequiredService<Application.Interfaces.Relay.IProtocolDiagnostics>()), TimeProvider.System, provider.GetRequiredService<ISiteIdentityService>());
        }));
        var study = await web.Identities.CreateAsync("书房"); var bedroom = await web.Identities.CreateAsync("卧室");
        var beforeBusiness = mock.Calls.Count;
        using (var first = await Send(web, study.Token, new string('a', 64), ("User-Agent", "claude-cli/1.9.9 (external, cli)"), ("x-app", "cli"), ("X-Stainless-OS", "MacOS"),
            ("X-Stainless-Arch", "arm64"), ("X-Stainless-Runtime-Version", "v22.1.0"), ("X-Stainless-Package-Version", "0.99.0"), ("X-Claude-Code-Session-Id", "session-study"),
            ("X-Device-Hint", "study-laptop"), ("anthropic-version", "2023-06-01"), ("anthropic-beta", "claude-code-20250219")))
            Check(first.StatusCode == HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        using (var second = await Send(web, bedroom.Token, new string('b', 64), ("User-Agent", "claude-cli/1.0.0 (external, cli)"), ("x-app", "vscode"), ("X-Stainless-OS", "Linux"),
            ("X-Stainless-Arch", "x64"), ("X-Stainless-Runtime-Version", "v18.0.0"), ("X-Stainless-Lang", "python"), ("X-Claude-Code-Session-Id", "session-bedroom"),
            ("anthropic-dangerous-direct-browser-access", "false"), ("anthropic-version", "2023-06-01"), ("anthropic-beta", "claude-code-20250219")))
            Check(second.StatusCode == HttpStatusCode.OK, await second.Content.ReadAsStringAsync());
        var relayed = mock.Calls.Where(call => call.Path.StartsWith("/v1/messages", StringComparison.Ordinal)).TakeLast(2).ToArray();
        foreach (var call in relayed)
        {
            foreach (var (name, value) in identity) Check(call.Headers.TryGetValue(name, out var sent) && sent == value, $"{name}: sent {sent}, real claude.exe sends {value}");
            Check(call.Headers.Keys.All(name => identity.ContainsKey(name) || _perRequest.Contains(name) || ClaudeCodeIdentity.RequestHeaders.Contains(name)), "Only the unified identity may leave: " + string.Join(", ", call.Headers.Keys));
            Check(call.Headers["Authorization"] == "Bearer " + Token && call.Body.Contains((await web.Settings.ReadAsync()).Value.DeviceId, StringComparison.Ordinal));
        }
        // 内容长度与按当次透传的头（会话编号、每请求新生成的请求编号等）本就每次不同，其余必须逐值相同。
        var shared = relayed[0].Headers.Keys.Where(name => name != "Content-Length" && !ClaudeCodeIdentity.RequestHeaders.Contains(name)).ToArray();
        Check(shared.All(name => relayed[1].Headers.TryGetValue(name, out var other) && other == relayed[0].Headers[name]), "Two clients must look like one device");
        Check(relayed[0].Headers.Keys.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(relayed[1].Headers.Keys.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase));
        Check(relayed[0].Headers["X-Claude-Code-Session-Id"] == "session-study" && relayed[1].Headers["X-Claude-Code-Session-Id"] == "session-bedroom", "Session ids follow the request body");
        Check(!relayed.Any(call => call.Body.Contains(new string('a', 64), StringComparison.Ordinal) || call.Body.Contains(new string('b', 64), StringComparison.Ordinal)));
        var host = await web.Identities.CreateAsync("MyCoAI 宿主");
        using (var third = await Send(web, host.Token, new string('c', 64), ("User-Agent", "claude-cli/0.7.7 (external, sdk-cli)"), ("x-app", "cli"), ("X-Stainless-OS", "Windows"),
            ("X-Claude-Code-Session-Id", "session-host"), ("anthropic-version", "2023-06-01")))
            Check(third.StatusCode == HttpStatusCode.OK, await third.Content.ReadAsStringAsync());
        var sdk = mock.Calls.Last();
        foreach (var (name, value) in print) Check(sdk.Headers.TryGetValue(name, out var sent) && sent == value, $"sdk-cli {name}: sent {sent}, real claude.exe --print sends {value}");
        Check(sdk.Headers.Keys.All(name => print.ContainsKey(name) || _perRequest.Contains(name) || ClaudeCodeIdentity.RequestHeaders.Contains(name)), "Only the sdk-cli identity may leave: " + string.Join(", ", sdk.Headers.Keys));
        var business = mock.Calls.Skip(beforeBusiness).ToArray();
        var callerDevices = new[] { new string('a', 64), new string('b', 64), new string('c', 64) };
        Check(business.Length == 3 && business.All(call => !callerDevices.Any(device => call.Body.Contains(device, StringComparison.Ordinal))),
            "Three simulated clients send exactly three original requests without leaking any original caller device.");
        Console.WriteLine($"Identity compared: {identity.Count} headers per mode from real claude.exe (cli {identity["User-Agent"]}; sdk-cli {print["User-Agent"]}), "
            + $"{shared.Length} headers equal across two cli clients, {print.Count} headers equal to --print for the sdk-cli client.");
        Console.WriteLine($"Relay identity comparison: simulated_clients=3; distinct_input_devices={callerDevices.Distinct(StringComparer.Ordinal).Count()}; observed_upstream_requests={business.Length}; original_device_occurrences=0.");
    }

    /// <summary>从一次实抓里取出身份头：去掉跟着每次请求或连接走的头，再去掉产品按当次透传的 <see cref="ClaudeCodeIdentity.RequestHeaders"/>（2.1.325 起两种模式都带提示编号与请求编号）。</summary>
    private static Dictionary<string, string> Identity(UpstreamCall call) =>
        call.Headers.Where(header => !_perRequest.Contains(header.Key) && !ClaudeCodeIdentity.RequestHeaders.Contains(header.Key)).ToDictionary(header => header.Key, header => header.Value, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 请求头分类（用户 2026-11-12 定的甲）：实抓只缓存稳定白名单；SDK 重试次数与子代理编号按客户端这一次的值发往官方，客户端不带就不带、不补造；
    /// 实抓里白名单外的头不缓存、不发往官方（客户端带了同名头也不放行），上游页读出「发现未分类请求头」与下一步，其余照常发送。
    /// </summary>
    public static async Task PerRequestHeadersFollowEachRequest()
    {
        var plain = FakeClaude.Identity("1.0.0");
        var parsed = ClaudeCodeIdentity.From([.. plain, new("X-Stainless-Retry-Count", "0"), new("X-Claude-Code-Agent-Id", "captured-agent"), new(FakeClaude.UnclassifiedHeader, "frozen")],
            "1.0.0", "sdk-cli", DateTimeOffset.UnixEpoch);
        Check(parsed.Headers.Count == plain.Length && parsed.Headers.All(header => ClaudeCodeIdentity.StableHeaders.Contains(header.Key)), "Only stable headers are cached: " + string.Join(", ", parsed.Headers.Select(header => header.Key)));
        Check(parsed.Unclassified.SequenceEqual([FakeClaude.UnclassifiedHeader]), "Unclassified: " + string.Join(", ", parsed.Unclassified));
        using var claude = new FakeClaudeLauncher("extra-headers");
        await using var mock = await UpstreamFixture.Create();
        await using var web = await Official(claude, mock);
        var person = await web.Identities.CreateAsync("书房");
        using (var first = await Send(web, person.Token, new string('a', 64), ("X-Stainless-Retry-Count", "2"), ("X-Claude-Code-Agent-Id", "agent-one"),
            ("X-Claude-Code-Parent-Agent-Id", "agent-root"), (FakeClaude.UnclassifiedHeader, "client-value"),
            ("X-Claude-Code-Request-Class", "main"), ("X-Claude-1p-Request-Class", "main"), ("X-Claude-Code-Prompt-Id", "11111111-2222-4333-8444-555555555555"),
            ("X-Client-Request-Id", "22222222-3333-4444-8555-666666666666"), ("X-Claude-Code-Sandy-Plover", "[]"),
            ("anthropic-beta", "zeta-2026-01-01,alpha-2026-02-02")))
            Check(first.StatusCode == HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        var retried = mock.Calls.Last().Headers;
        Check(retried["X-Stainless-Retry-Count"] == "2" && retried["X-Claude-Code-Agent-Id"] == "agent-one" && retried["X-Claude-Code-Parent-Agent-Id"] == "agent-root",
            "Per-request headers carry this request's values: " + string.Join(", ", retried.Select(header => header.Key + "=" + header.Value)));
        // 第 3 项：网关提示头按客户端当次的值透传（客户端开 CLAUDE_CODE_GATEWAY_HINT_HEADERS 才会带）。
        Check(retried["X-Claude-Code-Request-Class"] == "main" && retried["X-Claude-1p-Request-Class"] == "main"
            && retried["X-Claude-Code-Prompt-Id"] == "11111111-2222-4333-8444-555555555555", "Gateway hint headers pass through by this request's value");
        // 客户端认定地址是官方时才发的请求编号与工具来源列表（ASSUME_1P 回环实测）：按当次值透传。
        Check(retried["X-Client-Request-Id"] == "22222222-3333-4444-8555-666666666666" && retried["X-Claude-Code-Sandy-Plover"] == "[]", "First-party request headers pass through by this request's value");
        // 第 2 项：anthropic-beta 按客户端这一次的原序原样透传，中转不重排、不补造（oauth 补位另见 UpstreamVersionTests）。
        Check(retried["anthropic-beta"] == "zeta-2026-01-01,alpha-2026-02-02", "anthropic-beta keeps client order verbatim: " + retried["anthropic-beta"]);
        using (var second = await Send(web, person.Token, new string('a', 64), ("X-Stainless-Retry-Count", "0"))) Check(second.StatusCode == HttpStatusCode.OK);
        var main = mock.Calls.Last().Headers;
        Check(main["X-Stainless-Retry-Count"] == "0" && !main.ContainsKey("X-Claude-Code-Agent-Id") && !main.ContainsKey("X-Claude-Code-Parent-Agent-Id"), "A main-session request carries no agent ids");
        Check(!main.ContainsKey("X-Client-Request-Id") && !main.ContainsKey("X-Claude-Code-Sandy-Plover"), "First-party request headers the client did not send are not invented or replayed");
        using (var third = await Send(web, person.Token, new string('a', 64))) Check(third.StatusCode == HttpStatusCode.OK);
        Check(!mock.Calls.Last().Headers.ContainsKey("X-Stainless-Retry-Count"), "A retry count the client did not send must not be invented or replayed from the capture");
        Check(mock.Calls.All(call => !call.Headers.ContainsKey(FakeClaude.UnclassifiedHeader)), "An unclassified header never reaches the official upstream");
        // 取账户资料照原生登录只带 claude-code/版本，不是推理请求，不在统一身份之列。
        Check(mock.Calls.Where(call => call.Path != "/api/oauth/profile").All(call => call.Headers["X-Stainless-OS"] == "FakeOS" && call.Headers["User-Agent"].StartsWith("claude-cli/", StringComparison.Ordinal)), "Stable identity still goes out");
        await web.Login();
        var page = WebUtility.HtmlDecode(await web.Http.GetStringAsync(web.Url + "/Upstream"));
        Check(page.Contains("发现未分类请求头 " + FakeClaude.UnclassifiedHeader + "：", StringComparison.Ordinal) && page.Contains("请更新中转。", StringComparison.Ordinal), "The upstream page must read the unclassified notice");
        Check(!page.Contains("frozen-capture-value", StringComparison.Ordinal) && !page.Contains("client-value", StringComparison.Ordinal), "Header values are never shown");
        Console.WriteLine($"Header classification: stable_cached={parsed.Headers.Count}; official_requests={mock.Calls.Count}; unclassified_sent=0; retry_counts=2,0,absent; agent_ids_on_main=0.");
    }

    /// <summary>
    /// 家里 Claude Code 没开「按官方直连发请求」开关（对话请求不带 X-Client-Request-Id）发往官方账户：中转本地拒绝、400、读句含可复制执行的开关命令，模拟上游零请求；
    /// 非 Claude Code 客户端不受影响。开了开关（带 X-Client-Request-Id）照常通过。
    /// </summary>
    public static async Task FirstPartySwitchOffReadsInstruction()
    {
        using var claude = new FakeClaudeLauncher("success");
        await using var mock = await UpstreamFixture.Create();
        await using var web = await Official(claude, mock);
        var person = await web.Identities.CreateAsync("书房");
        var before = mock.Calls.Count;
        // claude-cli 客户端但没带 X-Client-Request-Id：视为未开开关。
        using (var off = await Send(web, person.Token, new string('a', 64), ("User-Agent", "claude-cli/2.1.323 (external, cli)"), ("X-Client-Request-Id", "")))
        {
            Check((int)off.StatusCode == 400, "A Claude Code request without the switch is refused locally: " + (int)off.StatusCode);
            var said = await Message(off);
            Check(said is not null && said.Contains("_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL", StringComparison.Ordinal) && said.Contains("SetEnvironmentVariable", StringComparison.Ordinal),
                "The message carries the copyable switch command: " + said);
        }
        Check(mock.Calls.Count == before, "A switched-off Claude Code request never reaches the official upstream");
        using var lease = await web.Pool.AcquireAsync();
        Check(!lease.Paused && lease.CooldownSeconds == 0, "The local refusal does not pause or cool down the upstream");
        // 非 Claude Code 客户端（无 claude-cli User-Agent）不判这一条，照旧放行。
        using (var other = await Send(web, person.Token, new string('a', 64), ("User-Agent", "some-sdk/1.0")))
            Check(other.StatusCode == HttpStatusCode.OK, "A non-Claude-Code client is not gated: " + await other.Content.ReadAsStringAsync());
        // 开了开关（带 X-Client-Request-Id）照常通过。
        using (var on = await Send(web, person.Token, new string('a', 64), ("User-Agent", "claude-cli/2.1.323 (external, cli)"), ("X-Client-Request-Id", Guid.NewGuid().ToString())))
            Check(on.StatusCode == HttpStatusCode.OK, "A switched-on Claude Code request passes: " + await on.Content.ReadAsStringAsync());
        // 这次的客户端比中转的假 Claude Code 新，升级器会在后台跑一次假升级，途中读代理设置要开库；等它结束再收尾，否则删数据目录会撞上它开着的库。
        var updater = web.App.Services.GetRequiredService<IClaudeCodeUpdater>();
        for (var deadline = DateTime.UtcNow.AddSeconds(30); updater.Describe(_ => "").Contains("正在后台升级", StringComparison.Ordinal); await Task.Delay(100))
            Check(DateTime.UtcNow < deadline, "Timed out waiting for the background upgrade");
    }

    /// <summary>
    /// 身份缺失时不发：官方账户在用，本机 Claude Code 被卸掉；客户端拿到 503 和「找不到 Claude Code」的读句，模拟上游一个请求都没收到，
    /// 不进认证暂停也不进冷却；用量页那一行与上游页读出同一句。
    /// </summary>
    public static async Task MissingIdentityNeverSends()
    {
        using var claude = new FakeClaudeLauncher("success");
        await using var mock = await UpstreamFixture.Create();
        await using var web = await Official(claude, mock);
        var before = mock.Calls.Count; claude.Reinstall("missing", claude.Version);
        var person = await web.Identities.CreateAsync("书房");
        using var response = await Send(web, person.Token, new string('a', 64), ("User-Agent", "claude-cli/9.9.9 (external, cli)"));
        Check(response.StatusCode == HttpStatusCode.ServiceUnavailable && await Message(response) == ClaudeCodeIdentity.NotFound);
        Check(mock.Calls.Count == before, "Nothing may reach the official upstream without the unified identity");
        await Same(web, ClaudeCodeIdentity.NotFound);
    }

    /// <summary>本机 Claude Code 升级后自动重取：管理员什么都不按，升级前发出去的是 1.0.0 的身份，升级后下一个请求就是 1.0.1 的身份，两次都照常成功。</summary>
    public static async Task UpgradeRecapturesIdentity()
    {
        using var claude = new FakeClaudeLauncher("success");
        await using var mock = await UpstreamFixture.Create();
        await using var web = await Official(claude, mock);
        var person = await web.Identities.CreateAsync("书房");
        using (var first = await Send(web, person.Token, new string('a', 64))) Check(first.StatusCode == HttpStatusCode.OK);
        Check(mock.Calls.Last().Headers["User-Agent"] == "claude-cli/1.0.0 (external, sdk-cli)", mock.Calls.Last().Headers["User-Agent"]);
        claude.Reinstall("success", "1.0.1");
        using (var second = await Send(web, person.Token, new string('a', 64))) Check(second.StatusCode == HttpStatusCode.OK);
        Check(mock.Calls.Last().Headers["User-Agent"] == "claude-cli/1.0.1 (external, sdk-cli)", "The upgraded identity must be used: " + mock.Calls.Last().Headers["User-Agent"]);
    }

    /// <summary>重取失败：本机 Claude Code 换了版本却发不出请求；客户端、用量页、上游页读到同一句「没能取到请求身份」，模拟上游一个请求都没收到，也没进暂停或冷却。</summary>
    public static async Task RecaptureFailureReadsSame()
    {
        using var claude = new FakeClaudeLauncher("success");
        await using var mock = await UpstreamFixture.Create();
        await using var web = await Official(claude, mock);
        var before = mock.Calls.Count; claude.Reinstall("no-identity", "1.0.2");
        var person = await web.Identities.CreateAsync("书房");
        using var response = await Send(web, person.Token, new string('a', 64));
        Check(response.StatusCode == HttpStatusCode.ServiceUnavailable && await Message(response) == ClaudeCodeIdentity.CaptureFailed);
        Check(mock.Calls.Count == before, "A failed recapture must not fall back to any other identity");
        await Same(web, ClaudeCodeIdentity.CaptureFailed);
    }

    /// <summary>
    /// API 密钥上游与兼容中转的账户令牌发出的请求与改动前逐字相同：同一组客户端头，API 密钥模式带客户端 User-Agent、去掉 x-stainless-*、保留其余；
    /// 兼容中转账户模式换成设置里的 User-Agent。逐个头名与取值钉死。
    /// </summary>
    public static async Task OtherModesUnchanged()
    {
        (string, string)[] client = [("User-Agent", "client-agent/1.0"), ("x-app", "cli"), ("X-Stainless-OS", "Windows"), ("X-Stainless-Arch", "x64"),
            ("X-Claude-Code-Session-Id", "session-1"), ("anthropic-dangerous-direct-browser-access", "true"), ("x-custom", "kept"),
            ("anthropic-version", "2023-06-01"), ("anthropic-beta", "future-beta,oauth-2025-04-20")];
        foreach (var (mode, authentication, agent, credential) in new[] { ("api-key", "x-api-key", "client-agent/1.0", "x-api-key"), ("oauth", "bearer", "HouseholdRelay/1.0", "Authorization") })
        {
            await using var mock = await UpstreamFixture.Create(); await using var web = await WebFixture.Create();
            await mock.Activate(web.Upstreams, "fixture-unchanged-secret", mock.Options(mode: mode, authentication: authentication));
            var person = await web.Identities.CreateAsync("书房");
            using (var response = await Send(web, person.Token, new string('a', 64), client)) Check(response.StatusCode == HttpStatusCode.OK);
            var sent = mock.Calls.Last().Headers;
            var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["x-app"] = "cli", ["X-Claude-Code-Session-Id"] = "session-1", ["anthropic-dangerous-direct-browser-access"] = "true", ["x-custom"] = "kept",
                ["anthropic-version"] = "2023-06-01", ["anthropic-beta"] = "future-beta", ["User-Agent"] = agent, ["Accept-Encoding"] = "identity", ["Content-Type"] = "application/json; charset=utf-8",
                [credential] = credential == "x-api-key" ? "fixture-unchanged-secret" : "Bearer fixture-unchanged-secret"
            };
            Check(sent.Keys.Where(name => name is not ("Host" or "Content-Length")).Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(expected.Keys.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase),
                mode + ": " + string.Join(", ", sent.Keys));
            foreach (var (name, value) in expected) Check(sent[name] == value, $"{mode} {name}: {sent[name]}");
        }
    }

    /// <summary>
    /// 官方账户的测试直接跑真 Claude Code：只有一次 Messages 调用，是假 Claude Code 发起、带统一身份的那一次，没有标准调用；获取模型也带统一身份；
    /// 中转自己的 HouseholdRelay/1.0 一次都没发往官方。
    /// </summary>
    public static async Task OfficialTestUsesClaudeCodeOnly()
    {
        using var claude = new FakeClaudeLauncher("success");
        await using var mock = await UpstreamFixture.Create();
        await using var web = await Official(claude, mock);
        var messages = mock.Calls.Where(call => call.Path.StartsWith("/v1/messages", StringComparison.Ordinal)).ToArray();
        Check(messages.Length == 1 && messages[0].Body.Contains("fixture system", StringComparison.Ordinal), "The official test must be one real Claude Code call");
        // 取账户资料照原生登录只带 claude-code/版本（另有专门用例核对），其余每个请求都带统一身份。
        Check(mock.Calls.Where(call => call.Path == "/api/oauth/profile").All(call => call.Headers["User-Agent"] == "claude-code/1.0.0"), "The profile request uses the native login User-Agent");
        foreach (var call in mock.Calls.Where(call => call.Path != "/api/oauth/profile"))
        {
            Check(call.Headers["User-Agent"] == "claude-cli/1.0.0 (external, sdk-cli)" && call.Headers["X-Stainless-OS"] == "FakeOS" && call.Headers["x-app"] == "cli", call.Path);
            Check(!call.Headers.Values.Any(value => value.Contains("HouseholdRelay/", StringComparison.Ordinal)), call.Path);
        }
        Check(mock.Calls.Any(call => call.Path.Contains("/models", StringComparison.Ordinal)), "Model discovery runs with the identity too");
    }

    /// <summary>测试被拒时读出上游原话：官方回 429，读句带「上游原话：rate_limit_error：…」，原话里的访问令牌被去掉，原话截到 200 字以内。</summary>
    public static async Task FailureQuotesUpstream()
    {
        using var claude = new FakeClaudeLauncher("success");
        await using var mock = await UpstreamFixture.Create();
        await using var web = await WebFixture.Create(overrides: claude.Register);
        mock.Handler = async context =>
        {
            if (context.Request.Path.Value!.EndsWith("/models", StringComparison.Ordinal)) { await UpstreamFixture.Success(context); return; }
            context.Response.StatusCode = 429;
            await context.Response.WriteAsJsonAsync(new { type = "error", error = new { type = "rate_limit_error", message = "Rejected for " + Token + ". " + new string('x', 400) } });
        };
        var id = await web.Upstreams.ImportOfficialAsync((await web.Upstreams.ReadActiveAsync()).ActiveVersion, Options(mock), FakeClaude.Credential(Token, 60), "官方", CancellationToken.None);
        var report = await web.Upstreams.TestCandidateAsync(id, true, CancellationToken.None);
        var quote = report.Message[report.Message.IndexOf("上游原话：", StringComparison.Ordinal)..];
        Check(report.Call == Core.Upstream.CheckStatus.Cooldown && report.Message.StartsWith("最小模型调用受到上游限流，请稍后重新测试。上游原话：rate_limit_error：Rejected for [redacted].", StringComparison.Ordinal), report.Message);
        Check(!report.Message.Contains(Token, StringComparison.Ordinal) && quote.Length <= "上游原话：".Length + 200 + 1 && quote.EndsWith("…。", StringComparison.Ordinal), quote);
    }

    /// <summary>
    /// 让本机真 claude.exe 直接打一个模拟端点（不经中转、不碰官方），返回它发出的第一个请求；隔离配置目录，用完删掉。
    /// <paramref name="interactive"/> 为真时用中转同一套交互模式参数在伪控制台里跑（不开窗口），否则自己组 <c>--print</c> 参数跑。
    /// </summary>
    private static async Task<UpstreamCall> Native(bool interactive)
    {
        if (interactive) return await NativeInteractive();
        await using var native = await UpstreamFixture.Create();
        native.Handler = context =>
        {
            context.Response.StatusCode = 400;
            return context.Response.WriteAsJsonAsync(new { type = "error", error = new { type = "invalid_request_error", message = "capture only" } });
        };
        var directory = Directory.CreateTempSubdirectory("hr-native-claude-").FullName;
        var start = ClaudeCodeProbe.Start(new NativeCliIsolationLauncher().FindClaude(), directory, native.Url, "fixture-native-token", null, new ProxyRoute(null, "127.0.0.1,localhost", "直连"), "sdk-cli");
        SeedNativeDevice(start);
        var launches = 0;
        using (var child = Process.Start(start)!)
        {
            launches++;
            child.StandardInput.Close();
            var output = child.StandardOutput.BaseStream.CopyToAsync(Stream.Null); var errors = child.StandardError.BaseStream.CopyToAsync(Stream.Null);
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (native.Calls.IsEmpty && !child.HasExited && DateTime.UtcNow < deadline) await Task.Delay(100);
            // 抓到第一个请求就结束整个子进程树，不等它重试。
            if (!child.HasExited) { child.Kill(true); await child.WaitForExitAsync(); }
            await Task.WhenAll(output, errors);
        }
        // 只删本用例在系统临时目录下建的隔离目录；子进程刚退出还占着文件时放弃，残留的只是临时配置。
        try { Directory.Delete(directory, true); }
        // 文件仍被占用：放弃清理，不让用例因此失败。
        catch (IOException) { }
        Check(!native.Calls.IsEmpty, "Real claude.exe must send one request to the local endpoint");
        return NativeEvidence(native, directory, "sdk-cli", launches);
    }

    /// <summary>本机真 claude.exe 的交互模式：参数与预写配置用中转的 <see cref="ClaudeCodeProbe.Start"/>，在伪控制台里跑，抓到第一个请求就结束。</summary>
    private static async Task<UpstreamCall> NativeInteractive()
    {
        await using var native = await UpstreamFixture.Create();
        native.Handler = context =>
        {
            context.Response.StatusCode = 400;
            return context.Response.WriteAsJsonAsync(new { type = "error", error = new { type = "invalid_request_error", message = "capture only" } });
        };
        var directory = Directory.CreateTempSubdirectory("hr-native-claude-").FullName;
        var start = ClaudeCodeProbe.Start(new NativeCliIsolationLauncher().FindClaude(), directory, native.Url, "fixture-native-token", null, new ProxyRoute(null, "127.0.0.1,localhost", "直连"), "cli");
        SeedNativeDevice(start);
        var launches = 0;
        using (var terminal = PseudoConsoleProcess.Start(start, new Dictionary<string, string> { ["Press Enter to continue"] = "\r" }))
        {
            launches++;
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (native.Calls.IsEmpty && !terminal.Process.HasExited && DateTime.UtcNow < deadline) await Task.Delay(100);
        }
        // 只删本用例在系统临时目录下建的隔离目录；子进程刚退出还占着文件时放弃，残留的只是临时配置。
        try { Directory.Delete(directory, true); }
        // 文件仍被占用：放弃清理，不让用例因此失败。
        catch (IOException) { }
        Check(!native.Calls.IsEmpty, "Real claude.exe in interactive mode must send one request to the local endpoint");
        return NativeEvidence(native, directory, "cli", launches);
    }

    /// <summary>只在刚创建的测试配置目录预置 userID；保留 Start 生成的引导、主题和信任字段，不触碰真实账户。</summary>
    private static void SeedNativeDevice(ProcessStartInfo start)
    {
        var directory = start.Environment["CLAUDE_CONFIG_DIR"]!;
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, ".claude.json");
        var json = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject() : new JsonObject();
        json["userID"] = _nativeDevice;
        File.WriteAllText(path, json.ToJsonString());
    }

    /// <summary>读取原生真实发出的首个请求，验证预置设备值；只输出启动数、实收请求数和设备/目录哈希，不改写请求。</summary>
    private static UpstreamCall NativeEvidence(UpstreamFixture native, string directory, string mode, int launches)
    {
        var first = native.Calls.First();
        using var body = JsonDocument.Parse(first.Body);
        var identifier = body.RootElement.GetProperty("metadata").GetProperty("user_id").GetString()!;
        string observed;
        if (identifier.StartsWith("user_", StringComparison.Ordinal))
        {
            Check(DeviceMetadata.LegacyId().IsMatch(identifier), "The native client must send a supported legacy identity.");
            observed = identifier.Substring(5, 64);
        }
        else { using var inner = JsonDocument.Parse(identifier); observed = inner.RootElement.GetProperty("device_id").GetString()!; }
        Check(launches == 1 && observed == _nativeDevice, "A new isolated native directory must send exactly its pre-seeded userID without request rewriting.");
        var deviceHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(observed)));
        var directoryHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(directory)));
        Console.WriteLine($"Native direct fixture: mode={mode}; fresh_directory_hash={directoryHash}; actual_process_launches={launches}; observed_requests={native.Calls.Count}; preseed_matches_wire=true; device_hash={deviceHash}.");
        return first;
    }

    /// <summary>带假 Claude Code 的网页主机里导入一个官方账户上游（指向模拟上游），测试通过并切换过去；<paramref name="extra"/> 可再覆盖服务登记。</summary>
    private static async Task<WebFixture> Official(FakeClaudeLauncher claude, UpstreamFixture mock, Action<IServiceCollection>? extra = null)
    {
        var web = await WebFixture.Create(overrides: services => { claude.Register(services); extra?.Invoke(services); });
        var id = await web.Upstreams.ImportOfficialAsync((await web.Upstreams.ReadActiveAsync()).ActiveVersion, Options(mock), FakeClaude.Credential(Token, 60), "官方", CancellationToken.None);
        var report = await web.Upstreams.TestCandidateAsync(id, true, CancellationToken.None);
        Check(report.CanActivate, report.Message); await web.Upstreams.ActivateAsync(id);
        return web;
    }

    /// <summary>指向模拟上游的官方账户配置：只给地址、私网开关与模型，账户模式与 Bearer 由服务定死。</summary>
    private static UpstreamOptions Options(UpstreamFixture mock) => new() { BaseUrl = mock.Url, AllowPrivateNetwork = true, Model = "fixture-model" };

    /// <summary>以个人令牌经中转发一条最小 Messages 请求，正文带 <paramref name="device"/> 作设备编号，另加 <paramref name="headers"/>。调用方释放响应。</summary>
    private static async Task<HttpResponseMessage> Send(WebFixture web, string token, string device, params (string Name, string Value)[] headers)
    {
        var userId = JsonSerializer.Serialize(new { device_id = device, account_uuid = "", session_id = "session-" + device[..4] });
        using var request = new HttpRequestMessage(HttpMethod.Post, web.Url + "/v1/messages?beta=true")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { model = "fixture-model", max_tokens = 16, messages = new[] { new { role = "user", content = "hi" } },
                metadata = new { user_id = userId } }), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new("Bearer", token);
        // 真实 Claude Code 客户端（User-Agent claude-cli）开了「按官方直连发请求」开关才发往官方，每个对话请求都带 X-Client-Request-Id；
        // 模拟 claude-cli 的请求默认补一个表示已开开关，调用方显式给了就用它的；非 claude-cli 的合成请求不补，用来验证「未发不补」。
        var claudeCli = headers.Any(h => h.Name.Equals("User-Agent", StringComparison.OrdinalIgnoreCase) && h.Value.StartsWith("claude-cli/", StringComparison.OrdinalIgnoreCase));
        if (claudeCli && !headers.Any(h => h.Name.Equals("X-Client-Request-Id", StringComparison.OrdinalIgnoreCase)))
            request.Headers.TryAddWithoutValidation("X-Client-Request-Id", Guid.NewGuid().ToString());
        foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);
        return await web.Http.SendAsync(request);
    }

    /// <summary>中转错误响应里的 error.message。</summary>
    private static async Task<string?> Message(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("error").GetProperty("message").GetString();
    }

    /// <summary>
    /// 拒发之后：在用的官方账户没进认证暂停也没进冷却；用量那一行记「未发出」、尝试记 identity_unavailable 和这句原因；用量页与上游页都读得出这句原因。
    /// </summary>
    private static async Task Same(WebFixture web, string sentence)
    {
        var state = web.Pool.Status((await web.Upstreams.ReadActiveAsync()).ActiveVersion);
        Check(!state.Paused && state.CooldownSeconds == 0, "Refusing to send is not the official upstream's fault");
        var report = await Settled(web, 1); var row = report.Rows.Single(); var attempt = report.AttemptsOf(row.RequestId).Single();
        Check(row is { Outcome: Core.Usage.UsageOutcome.Failed, Dispatched: false } && attempt.ErrorType == "identity_unavailable" && attempt.ErrorMessage == sentence, JsonSerializer.Serialize(attempt));
        await web.Login();
        Check(WebUtility.HtmlDecode(await web.Http.GetStringAsync(web.Url + "/Usage")).Contains(sentence, StringComparison.Ordinal), "The usage page must read the same sentence");
        Check(WebUtility.HtmlDecode(await web.Http.GetStringAsync(web.Url + "/Upstream")).Contains(sentence, StringComparison.Ordinal), "The upstream page must read the same sentence");
    }
}
