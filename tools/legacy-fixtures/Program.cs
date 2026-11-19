// 旧库夹具驱动：编译时引用某个旧提交的源码工程，只调那一版的公开服务（与测试夹具 TestProcess 同一个组装入口 AddHouseholdRelay），
// 在 args[0] 指定的数据目录里生成一份真实的旧版数据库。条件编译符号 FIXTURE_V3 选版本 3 时期的接口（提交 39c6785），否则按版本 8（提交 2dd823c）。
// 由 New-LegacyFixture.ps1 复制到临时目录、生成工程文件后编译运行；不连网、不读真实凭据，密钥都是写死的假值。
using System.Net.Http;
using Application.Interfaces.Identity;
using Application.Interfaces.Upstream;
using Application.Interfaces.Usage;
using Core.Upstream;
using Core.Usage;
using HouseholdRelay.Composition;
using Microsoft.Extensions.DependencyInjection;

var home = args.Length == 1 ? Path.GetFullPath(args[0]) : throw new InvalidOperationException("用法：LegacyFixtureDriver 数据目录");
if (Directory.Exists(home) && Directory.EnumerateFileSystemEntries(home).Any()) throw new InvalidOperationException("数据目录必须是空的。");
using (var provider = new ServiceCollection().AddHouseholdRelay(options => options.Home = home).BuildServiceProvider())
{
    var identities = provider.GetRequiredService<IIdentityService>();
    var upstreams = provider.GetRequiredService<IUpstreamService>();
    var pricing = provider.GetRequiredService<IUsagePricingService>();
    var usage = provider.GetRequiredService<IUsageService>();
    var first = identities.Create("夹具甲"); var second = identities.Create("夹具乙"); identities.Rotate(second.UserId);
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
