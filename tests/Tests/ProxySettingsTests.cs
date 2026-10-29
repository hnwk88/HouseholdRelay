using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Infrastructure.ExternalSources.ClaudeCode;
using Infrastructure.ExternalSources.Network;
using Microsoft.AspNetCore.Http;
using static Tests.TestSuite;

namespace Tests;

/// <summary>全局代理三态、启用却取不到时拒绝、子进程代理变量先清后写。系统代理用假实现控制，不依赖测试机真实设置。</summary>
static class ProxySettingsTests
{
    /// <summary>
    /// 验证三态解析：不使用代理时直连；手动时用设置里的地址（补齐 http://）；自动时取系统对该目标的 HTTPS 代理并带上 NO_PROXY；
    /// 手动没填、自动被系统要求直连、自动取到的不是 HTTP 根地址时都抛出拒绝，读句写明「为防止意外直连，请求没有发出」；设置页读句不抛异常；
    /// 设置校验拒绝手动却没有地址和未知方式。
    /// </summary>
    public static Task ThreeStates()
    {
        var config = new RelayConfiguration(); var system = new FakeProxy("http://127.0.0.1:7897");
        var proxy = new SystemUpstreamProxy(() => config, system); var target = new Uri("https://api.example.test/");
        var direct = proxy.Resolve(target);
        Check(direct.Address is null && direct.NoProxy.Contains("127.0.0.1") && proxy.Describe() == "不使用代理，直连上游。");
        config.ProxyMode = "manual"; config.ProxyUrl = "127.0.0.1:8899";
        var manual = proxy.Resolve(target);
        Check(manual.Address?.AbsoluteUri == "http://127.0.0.1:8899/" && manual.Description == "手动指定 http://127.0.0.1:8899");
        config.ProxyMode = "auto";
        var automatic = proxy.Resolve(target);
        Check(automatic.Address?.AbsoluteUri == "http://127.0.0.1:7897/" && proxy.Describe() == "自动检测到 http://127.0.0.1:7897。" && automatic.NoProxy.Contains("::1"));
        Check(system.Asked.Contains("https://api.example.test/"), "Auto detection must ask the system for the real target");
        system.Bypass = true;
        Refused(() => proxy.Resolve(target), "系统没有为上游地址提供代理");
        Check(proxy.Describe().Contains("为防止意外直连，请求没有发出"));
        system.Bypass = false; system.Address = "socks5://127.0.0.1:7898";
        Refused(() => proxy.Resolve(target), "不是 HTTP 代理根地址");
        config.ProxyMode = "manual"; config.ProxyUrl = null;
        Refused(() => proxy.Resolve(target), "没有填写代理地址");
        CheckInvalid(new RelayConfiguration { ProxyMode = "manual" });
        CheckInvalid(new RelayConfiguration { ProxyMode = "sometimes" });
        new RelayConfiguration { ProxyMode = "auto" }.Validate();
        return Task.CompletedTask;
    }

    /// <summary>
    /// 验证启用代理却取不到地址时拒绝发出请求：中转转发返回 502 并说明代理不可用、模拟上游一次都没被访问；
    /// 获取模型读出代理原因、同样不访问上游；完整服务上那次被拒的请求在用量里记成未发出的失败（未派发、上游状态为空、金额不适用）。
    /// </summary>
    public static async Task RefusedWhenUnavailable()
    {
        var switchable = new RelayConfiguration();
        await using (var target = await UpstreamFixture.Create())
        await using (var web = await WebFixture.Create(overrides: services => services.AddSingleton<IUpstreamProxy>(
            new SystemUpstreamProxy(() => switchable, new FakeProxy("http://127.0.0.1:7897") { Bypass = true }))))
        {
            await target.Activate(web.Upstreams); var calls = target.Calls.Count; var person = web.Identities.Create("代理用量");
            switchable.ProxyMode = "auto";
            using (var refused = await UpstreamFixture.RelayRequest(web, person.Token)) Check((int)refused.StatusCode == 502 && target.Calls.Count == calls);
            var usage = web.App.Services.GetRequiredService<IUsageService>(); UsageRow? row = null;
            await Until(() => (row = usage.Query(new()).Rows.SingleOrDefault()) is { Outcome: not "started" });
            Check(row is { Outcome: "failed", Dispatched: false, UpstreamStatus: null, StatusCode: 502 } && row.Money.Source == "not_applicable", JsonSerializer.Serialize(row));
        }

        var config = new RelayConfiguration { ProxyMode = "auto" };
        var unavailable = new SystemUpstreamProxy(() => config, new FakeProxy("http://127.0.0.1:7897") { Bypass = true });
        await using var fixture = await Fixture.Create(context => context.Response.WriteAsync("{}"), proxy: unavailable);
        using var response = await fixture.Send(); var body = await response.Content.ReadAsStringAsync();
        Check((int)response.StatusCode == 502 && body.Contains("proxy is enabled but unavailable") && fixture.Calls.IsEmpty, body);
        await using var mock = await UpstreamFixture.Create();
        var probe = new UpstreamProbe(new AbsentClaudeCode(), unavailable);
        var catalog = await probe.Discover(new UpstreamSnapshot(1, mock.Options() with { AuthenticationManual = false }, "fixture-proxy-secret"), CancellationToken.None);
        Check(catalog.Status == "failed" && catalog.Message.Contains("已选择自动检测系统代理") && mock.Calls.IsEmpty, catalog.Message);
    }

    /// <summary>
    /// 验证子进程代理变量先清后写：继承来的大小写代理变量与 ALL_PROXY 全部清掉；不使用代理时只留含回环地址的 NO_PROXY；
    /// 有代理时 HTTP(S)_PROXY 写成设置里的地址；Claude Code 检测子进程照此组装，并保留它自己的连接变量。
    /// </summary>
    public static Task ChildEnvironment()
    {
        ProcessStartInfo Inherited()
        {
            var start = new ProcessStartInfo("claude.exe");
            start.Environment["HTTP_PROXY"] = "http://inherited.invalid:1"; start.Environment["https_proxy"] = "http://inherited.invalid:2";
            start.Environment["ALL_PROXY"] = "socks5://inherited.invalid:3"; start.Environment["NO_PROXY"] = "inherited.invalid";
            return start;
        }
        var direct = Inherited(); ChildProxyEnvironment.Apply(direct, new(null, "localhost,127.0.0.1,::1", "不使用代理"));
        Check(!direct.Environment.ContainsKey("HTTP_PROXY") && !direct.Environment.ContainsKey("HTTPS_PROXY") && !direct.Environment.ContainsKey("ALL_PROXY"));
        Check(direct.Environment["NO_PROXY"] == "localhost,127.0.0.1,::1" && direct.Environment.Values.All(value => value?.Contains("inherited") != true));
        var proxied = Inherited(); ChildProxyEnvironment.Apply(proxied, new(new Uri("http://127.0.0.1:7897/"), "localhost,127.0.0.1,::1", "自动检测到"));
        Check(proxied.Environment["HTTP_PROXY"] == "http://127.0.0.1:7897" && proxied.Environment["https_proxy"] == "http://127.0.0.1:7897" && !proxied.Environment.ContainsKey("ALL_PROXY"));
        var probe = ClaudeCodeProbe.Start("claude.exe", Path.GetTempPath(), "http://127.0.0.1:1", "local-token", "fixture-model", new(new Uri("http://127.0.0.1:7897/"), "localhost,127.0.0.1,::1", "手动指定"));
        Check(probe.Environment["HTTPS_PROXY"] == "http://127.0.0.1:7897" && probe.Environment["NO_PROXY"]!.Contains("127.0.0.1") && probe.Environment["ANTHROPIC_BASE_URL"] == "http://127.0.0.1:1");
        return Task.CompletedTask;
    }

    /// <summary>断言给定解析以「代理不可用」拒绝，且读句含 <paramref name="reason"/> 与防直连说明。</summary>
    private static void Refused(Action action, string reason)
    {
        try { action(); }
        // 只接受代理不可用的拒绝；读句须说明原因并写明请求没有发出。
        catch (UpstreamProxyUnavailableException error) { Check(error.Message.Contains(reason) && error.Message.Contains("为防止意外直连"), error.Message); return; }
        throw new Exception("Expected the proxy to refuse: " + reason);
    }

    /// <summary>断言设置校验拒绝这份代理配置。</summary>
    private static void CheckInvalid(RelayConfiguration config)
    {
        try { config.Validate(); }
        // 设置校验以 InvalidOperationException 拒绝即为预期。
        catch (InvalidOperationException) { return; }
        throw new Exception("Expected invalid proxy settings to be rejected");
    }

    /// <summary>可控的系统代理：记录被问到的目标，按 <see cref="Bypass"/> 决定是否要求直连。</summary>
    private sealed class FakeProxy(string address) : IWebProxy
    {
        /// <summary>系统返回的代理地址。</summary>
        public string Address { get; set; } = address;
        /// <summary>为真时模拟系统要求这个目标直连。</summary>
        public bool Bypass { get; set; }
        /// <summary>被问到的目标地址，用来确认自动检测按真实目标询问。</summary>
        public List<string> Asked { get; } = [];
        /// <summary>接口要求的凭据属性；测试不用。</summary>
        public ICredentials? Credentials { get; set; }
        /// <summary>记下目标并返回 <see cref="Address"/>。</summary>
        public Uri? GetProxy(Uri destination) { Asked.Add(destination.AbsoluteUri); return new Uri(Address); }
        /// <summary>按 <see cref="Bypass"/> 回答是否直连。</summary>
        public bool IsBypassed(Uri host) => Bypass;
    }

    /// <summary>本机没装 Claude Code 的替身：不会被调用。</summary>
    private sealed class AbsentClaudeCode : IClaudeCodeProbe
    {
        /// <summary>始终报告没有安装。</summary>
        public bool IsAvailable => false;
        /// <summary>不应被调用；被调用即抛出。</summary>
        public Task<Application.Contracts.Upstream.ProbeReply> Run(IUpstreamLease transport, CancellationToken cancellation) => throw new InvalidOperationException("未安装");
    }
}
