// 旧库夹具驱动：编译时引用某个旧提交的源码工程，只调那一版的公开服务（与测试夹具 TestProcess 同一个组装入口 AddHouseholdRelay），
// 在 args[0] 指定的数据目录里生成一份真实的旧版数据库。条件编译符号 FIXTURE_V3 选版本 3 时期的接口（提交 39c6785），FIXTURE_V9 选版本 9（提交 bb20b32），
// 都没有时按版本 8（提交 2dd823c）。由 New-LegacyFixture.ps1 复制到临时目录、生成工程文件后编译运行；不连网、不读真实凭据，密钥都是写死的假值。
using System.Net.Http;
using Application.Interfaces.Identity;
using Application.Interfaces.Upstream;
using Application.Interfaces.Usage;
using Core.Upstream;
using Core.Usage;
using HouseholdRelay.Composition;
using Microsoft.Extensions.DependencyInjection;
#if FIXTURE_V9
using System.Text.Json;
using Application.Interfaces.Administration;
using Application.Interfaces.ClaudeCode;
using Infrastructure.ExternalSources.ClaudeCode;

// 版本 9 的官方账户导入要跑一次 claude auth status：本驱动自己扮演那个 claude，只回答这一条。
if (args.Length > 0 && args[0] == "--fake-claude") Environment.Exit(FixtureClaude.Run(args[1..]));
#endif

var home = args.Length == 1 ? Path.GetFullPath(args[0]) : throw new InvalidOperationException("用法：LegacyFixtureDriver 数据目录");
if (Directory.Exists(home) && Directory.EnumerateFileSystemEntries(home).Any()) throw new InvalidOperationException("数据目录必须是空的。");
var services = new ServiceCollection().AddHouseholdRelay(options => options.Home = home);
#if FIXTURE_V9
using var claude = new FixtureClaude();
services.AddSingleton<IClientLauncher>(claude); services.AddSingleton(new OfficialLoginOptions { ShowWindow = false });
#endif
using (var provider = services.BuildServiceProvider())
{
    var identities = provider.GetRequiredService<IIdentityService>();
    var upstreams = provider.GetRequiredService<IUpstreamService>();
    var pricing = provider.GetRequiredService<IUsagePricingService>();
    var usage = provider.GetRequiredService<IUsageService>();
    var first = identities.Create("夹具甲"); var second = identities.Create("夹具乙"); var secondToken = identities.Rotate(second.UserId).Token;
#if FIXTURE_V9
    var firstToken = identities.Rotate(first.UserId).Token;
#endif
    var access = identities.ReadAccess().ToDictionary(item => item.UserId!);
    var started = DateTimeOffset.UtcNow.AddHours(-2);
#if FIXTURE_V3
    // 版本 3：单价按活动版本计；上游候选带各自的代理地址（升到 4 时只有一个能成为全局代理，另一个会被丢）。
    const long Version = 1;
    upstreams.SaveCandidate(Version, new UpstreamOptions { BaseUrl = "https://relay-a.example.com", ProxyUrl = "http://127.0.0.1:7890", Model = "claude-fixture" }, "sk-fixture-a-0001");
    upstreams.SaveCandidate(Version, new UpstreamOptions { BaseUrl = "https://relay-b.example.com", ProxyUrl = "http://127.0.0.1:7891", Model = "claude-fixture" }, "sk-fixture-b-0002");
    upstreams.SaveCandidate(Version, new UpstreamOptions { BaseUrl = "https://relay-c.example.com", Model = "claude-fixture" }, "sk-fixture-c-0003");
    pricing.Save(Version, "claude-fixture", "USD", 3m, 15m, 0.3m, 3.75m);
    pricing.Save(Version, "claude-other", "USD", 1m, 5m, null, null);
    for (var index = 0; index < 6; index++)
    {
        var id = "fixture-v3-" + index;
        await usage.Start(id, access[index % 2 == 0 ? first.UserId : second.UserId], "inference");
        if (index == 5) { await usage.Finish(id, new UsageEnd("failed", false, 502, null, 0, "not_applicable", new UsageTokens())); continue; }
        await usage.Dispatch(id, Version, index % 3 == 0 ? "claude-other" : "claude-fixture");
        await usage.Finish(id, new UsageEnd("succeeded", true, 200, 200, 800 + index, "complete", new UsageTokens(100 + index, 20, 0, 0)));
    }
#elif FIXTURE_V9
    // 版本 9：两人各一张有效令牌、一张轮换下来的旧令牌；三家 API 上游（一家停用）加一家导入登录文件的官方账户；四条单价；
    // 一条请求自动切换过一次（两次尝试）、一条没发出；一个管理员会话；一份只含两家 API 上游的导出文件（不导官方账户，导出会把它记成已搬走）。
    var administrators = provider.GetRequiredService<IAdministratorService>();
    const string Password = "fixture-v9-admin-password";
    administrators.Initialize(administrators.BootstrapCode()!, Password);
    var session = administrators.Login(Password) ?? throw new InvalidOperationException("管理员登录失败。");
    // 正式启动时由后台服务做站点设备准备；驱动不起网页主机，照它调一次，官方账户才能接入。
    await provider.GetRequiredService<Application.Interfaces.Configuration.ISiteIdentityService>().Prepare(CancellationToken.None);
    var version = upstreams.ReadOverview().Version;
    string Save(string name, string host, string key) => upstreams.SaveCandidate(version, new UpstreamOptions { BaseUrl = "https://" + host, Model = "claude-fixture" }, key, name);
    var kept = Save("夹具甲供应商", "relay-a.example.com", "sk-fixture-a-0001");
    var standby = Save("夹具乙供应商", "relay-b.example.com", "sk-fixture-b-0002");
    var removed = Save("夹具丙供应商", "relay-c.example.com", "sk-fixture-c-0003");
    var login = JsonSerializer.SerializeToUtf8Bytes(new
    {
        claudeAiOauth = new { accessToken = "fixture-v9-official-token", refreshToken = "fixture-v9-refresh-token", expiresAt = DateTimeOffset.UtcNow.AddDays(3650).ToUnixTimeMilliseconds(), scopes = new[] { "user:inference" } }
    });
    var official = await upstreams.ImportOfficial(version, new UpstreamOptions { BaseUrl = "https://api.anthropic.com" }, login, "夹具官方账户", CancellationToken.None);
    foreach (var (candidate, input) in new[] { (kept, 3m), (standby, 2m), (removed, 1m), (official, 15m) }) pricing.Save(candidate, "claude-fixture", "USD", input, input * 5, null, null);
    string[] order = [kept, standby, removed, official, standby, kept];
    for (var index = 0; index < order.Length; index++)
    {
        var id = "fixture-v9-" + index; var candidate = order[index];
        await usage.Start(id, access[index % 2 == 0 ? first.UserId : second.UserId], "inference");
        await usage.Dispatch(id, version, candidate, "claude-fixture");
        var attempts = new List<UpstreamAttempt>();
        if (index == 4)
        {
            // 先试乙连不上，自动切换到甲成功：用量归甲，两次尝试各归各家。
            var failed = new UpstreamAttempt(1, standby, version, DateTimeOffset.UtcNow);
            failed.Failed(new HttpRequestException("fixture"), false, text => text); failed.PassOn(); attempts.Add(failed);
            candidate = kept;
        }
        var served = new UpstreamAttempt(attempts.Count + 1, candidate, version, DateTimeOffset.UtcNow);
        served.Headers(200, "req_fixture_v9_" + index, text => text); attempts.Add(served);
        await usage.Finish(id, new UsageEnd("succeeded", true, 200, 200, 800 + index, "complete", new UsageTokens(100 + index, 20, 0, 0)) { Attempts = attempts });
    }
    var undispatched = "fixture-v9-undispatched";
    await usage.Start(undispatched, access[second.UserId], "inference");
    await usage.Finish(undispatched, new UsageEnd("failed", false, 502, null, 0, "not_applicable", new UsageTokens()));
    upstreams.Delete(removed);
    const string ExportPassword = "fixture-v9-export-password";
    File.WriteAllBytes(Path.Combine(home, "upstreams.export"), upstreams.Export([kept, standby], ExportPassword).File);
    // 夹具里的明文都是假值：升级用例拿它核对升级前发出的个人令牌、管理员会话照常可用，导出文件照常能导入。
    File.WriteAllText(Path.Combine(home, "plain.json"), JsonSerializer.Serialize(new
    {
        session, exportPassword = ExportPassword, tokens = new Dictionary<string, string> { ["夹具甲"] = firstToken, ["夹具乙"] = secondToken },
        upstreams = new Dictionary<string, string> { ["kept"] = kept, ["standby"] = standby, ["removed"] = removed, ["official"] = official }
    }, new JsonSerializerOptions { WriteIndented = true }));
#else
    // 版本 8：单价按已保存上游计；一家改过名、一家被删（物理删除，留下孤儿单价和孤儿用量引用），一条请求自动切换过一次。
    var version = upstreams.ReadOverview().Version;
    string Save(string name, string host, string key) => upstreams.SaveCandidate(version, new UpstreamOptions { BaseUrl = "https://" + host, Model = "claude-fixture" }, key, name);
    var kept = Save("夹具甲供应商", "relay-a.example.com", "sk-fixture-a-0001");
    var renamed = Save("夹具乙供应商", "relay-b.example.com", "sk-fixture-b-0002");
    var removed = Save("夹具丙供应商", "relay-c.example.com", "sk-fixture-c-0003");
    pricing.Save(kept, "claude-fixture", "USD", 3m, 15m, 0.3m, 3.75m);
    pricing.Save(renamed, "claude-fixture", "USD", 2m, 10m, null, null);
    pricing.Save(removed, "claude-fixture", "USD", 1m, 5m, null, null);
    pricing.Save(removed, "claude-other", "USD", 1m, 5m, null, null);
    string[] order = [kept, renamed, removed, kept, removed, renamed];
    for (var index = 0; index < order.Length; index++)
    {
        var id = "fixture-v8-" + index; var candidate = order[index];
        await usage.Start(id, access[index % 2 == 0 ? first.UserId : second.UserId], "inference");
        await usage.Dispatch(id, version, candidate, "claude-fixture");
        var attempts = new List<UpstreamAttempt>();
        if (index == 4)
        {
            // 先试被删的那家连不上，换到保留的那家成功。
            var failed = new UpstreamAttempt(1, removed, version, started.AddMinutes(index));
            failed.Failed(new HttpRequestException("fixture"), false, text => text); failed.PassOn(); attempts.Add(failed);
            candidate = kept;
        }
        var served = new UpstreamAttempt(attempts.Count + 1, candidate, version, started.AddMinutes(index));
        served.Headers(200, "req_fixture_" + index, text => text); attempts.Add(served);
        await usage.Finish(id, new UsageEnd("succeeded", true, 200, 200, 800 + index, "complete", new UsageTokens(100 + index, 20, 0, 0)) { Attempts = attempts });
    }
    var undispatched = "fixture-v8-undispatched";
    await usage.Start(undispatched, access[first.UserId], "inference");
    await usage.Finish(undispatched, new UsageEnd("failed", false, 502, null, 0, "not_applicable", new UsageTokens()));
    upstreams.Rename(renamed, "夹具乙供应商改名");
    upstreams.Delete(removed);
#endif
}
Console.WriteLine("已生成：" + home);

#if FIXTURE_V9
/// <summary>
/// 版本 9 夹具用的 Claude Code 定位器：<see cref="FindClaude"/> 返回临时目录里的 claude.cmd，它以 <c>--fake-claude</c> 调回本驱动；
/// 本驱动只回答导入要跑的 <c>auth status --json</c>（已登录、假邮箱），其余命令一律退出码 2。临时目录不在数据目录里，结束即删。
/// </summary>
sealed class FixtureClaude : IClientLauncher, IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("hr-fixture-claude-").FullName;

    /// <summary>扮演 claude：参数是 claude 自己收到的那几个。</summary>
    public static int Run(string[] arguments)
    {
        if (string.Join(' ', arguments) != "auth status --json") return 2;
        Console.WriteLine(JsonSerializer.Serialize(new { loggedIn = true, authMethod = "claude.ai", email = "fixture.official@example.com" }));
        return 0;
    }

    /// <summary>写好并返回调回本驱动的 claude.cmd。</summary>
    public string FindClaude()
    {
        var path = Path.Combine(directory, "claude.cmd");
        if (!File.Exists(path)) File.WriteAllText(path, $"@\"{Environment.ProcessPath}\" --fake-claude %*\r\n");
        return path;
    }

    /// <summary>导入用不到。</summary>
    public Task<string> DetectUserAgent() => Task.FromResult("claude-cli/1.0.0 (external, cli)");

    /// <summary>导入用不到。</summary>
    public System.Diagnostics.ProcessStartInfo Prepare(string bundleDirectory, IEnumerable<string> arguments) => throw new NotSupportedException();

    /// <summary>导入用不到。</summary>
    public Task<int> Run(string directory, string[] arguments) => throw new NotSupportedException();

    /// <summary>删掉临时的 claude.cmd 目录。</summary>
    public void Dispose()
    {
        try { Directory.Delete(directory, true); }
        // 假进程还没完全退出占着文件时放弃，残留的只是系统临时目录里一个小脚本。
        catch (IOException) { }
    }
}
#endif
