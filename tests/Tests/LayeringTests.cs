using System.Net;
using System.Text.Json;
using Application.Contracts.Uploads;
using Application.Interfaces.ClaudeCode;
using Application.Services.Relay;
using Core.Configuration;
using Core.Relay;
using Core.Upstream;
using Core.Usage;
using Core.Values;
using HouseholdRelay.Commands;
using HouseholdRelay.Management;
using HouseholdRelay.Pages;
using Infrastructure.ExternalSources.Network;
using Infrastructure.ExternalSources.Upstream;
using Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using static Tests.TestSuite;

namespace Tests;

/// <summary>分层归位的用例：封闭取值、必填依赖、模型名、各处只定义一次的上限与清单、分页与显示名、退出码。每条对应评估的一个编号，撤掉对应改动就会变红。</summary>
static class LayeringTests
{
    /// <summary>P-037：每种封闭取值在 JSON 里写出今天的那串字符、读回原值；不认识的字符串与数字都拒绝，消息列出全部可选项。</summary>
    public static Task ClosedValuesKeepWireNames()
    {
        RoundTrip<UsageKind>(); RoundTrip<UsageOutcome>(); RoundTrip<UsageCompleteness>(); RoundTrip<MoneySource>(); RoundTrip<MoneyCompleteness>();
        RoundTrip<AttemptDisposition>(); RoundTrip<ConnectFailure>(); RoundTrip<CandidateState>(); RoundTrip<CheckStatus>(); RoundTrip<CatalogStatus>();
        RoundTrip<AvoidanceKind>(); RoundTrip<UpstreamMode>(); RoundTrip<AuthenticationScheme>(); RoundTrip<UpstreamPreset>(); RoundTrip<DiagnosticMethod>(); RoundTrip<ProxyMode>();
        Check(Wire<UsageCompleteness>.Name(UsageCompleteness.NotProvided) == "not_provided" && Wire<UpstreamMode>.Name(UpstreamMode.ApiKey) == "api-key"
            && Wire<DiagnosticMethod>.Name(DiagnosticMethod.ClaudeCode) == "claude-code" && Wire<MoneySource>.Name(MoneySource.NotApplicable) == "not_applicable",
            "Stored strings must stay byte-for-byte the same");
        var report = JsonSerializer.Serialize(new UpstreamReport(DateTimeOffset.UnixEpoch, CheckStatus.Passed, CheckStatus.Reconnect, CheckStatus.Unsupported, CheckStatus.Cancelled, [], null, "m"), ConfigurationFiles.Json);
        Check(report.Contains("\"connection\": \"passed\"") && report.Contains("\"authentication\": \"reconnect\"") && report.Contains("\"call\": \"cancelled\""), report);
        return Task.CompletedTask;
    }

    /// <summary>一种封闭取值的往返与拒绝。</summary>
    private static void RoundTrip<TEnum>() where TEnum : struct, Enum
    {
        foreach (var value in Wire<TEnum>.Values)
        {
            var json = JsonSerializer.Serialize(value);
            Check(json == "\"" + Wire<TEnum>.Name(value) + "\"" && JsonSerializer.Deserialize<TEnum>(json).Equals(value), typeof(TEnum).Name + " " + json);
        }
        foreach (var bad in new[] { "\"mystery\"", "0" })
        {
            try { JsonSerializer.Deserialize<TEnum>(bad); Check(false, typeof(TEnum).Name + " accepted " + bad); }
            // 这里是用例期望的拒绝：接住后核对读句或类型，别的异常照常让用例失败。
            catch (JsonException error) { Check(error.Message.Contains("可选：" + Wire<TEnum>.Names[0], StringComparison.Ordinal), error.Message); }
        }
    }

    /// <summary>P-037：库里读到不认识的状态字符串时拒绝并列出可选项，不兜底成某个状态。</summary>
    public static async Task UnknownStoredValueIsRefused()
    {
        using var home = new TestHome(); var process = new TestProcess(home.Path);
        var token = await process.Identities.Create("未知状态"); var user = (await process.Identities.Clients.Authenticate(token.Token))!;
        await process.Usage.Start("mystery-1", user, UsageKind.Inference);
        using (var connection = process.Database.Open())
            RelayDatabase.Execute(connection, null, "UPDATE usage_requests SET outcome='mystery' WHERE request_id='mystery-1'");
        string? message = null;
        try { await process.Usage.Query(new()); }
        // 这里是用例期望的拒绝：接住后核对读句或类型，别的异常照常让用例失败。
        catch (Exception error) { message = ExceptionChain.Find<InvalidOperationException>(error) is { } found ? Messages(error) : error.Message; }
        Check(message is not null && message.Contains("不认识的UsageOutcome取值「mystery」", StringComparison.Ordinal) && message.Contains("可选：succeeded", StringComparison.Ordinal), message ?? "Unknown outcome was read");
    }

    /// <summary>异常链上所有消息连成一句，便于找到最里面那层的读句。</summary>
    private static string Messages(Exception error)
    {
        var parts = new List<string>();
        for (Exception? inner = error; inner is not null; inner = inner.InnerException) parts.Add(inner.Message);
        return string.Join(" | ", parts);
    }

    /// <summary>P-051：不认识的代理方式不再按直连处理，而是拒绝并列出可选项。</summary>
    public static Task UnknownProxyModeIsRefused()
    {
        var proxy = new SystemUpstreamProxy(() => new RelayConfiguration { DeviceId = new string('e', 64), ProxyMode = (ProxyMode)99 });
        try { proxy.Resolve(new Uri("https://api.example.test/")); Check(false, "Unknown proxy mode went direct"); }
        // 这里是用例期望的拒绝：接住后核对读句或类型，别的异常照常让用例失败。
        catch (InvalidOperationException error) { Check(error.Message.Contains("可选：none、auto、manual", StringComparison.Ordinal), error.Message); }
        return Task.CompletedTask;
    }

    /// <summary>P-049/P-079：获取模型失败时状态码放在单独一格，5xx 读出「暂时不可用」并带代理说明，其余读「检查地址」；消息不再暂存状态码。</summary>
    public static async Task CatalogStatusCodeHasItsOwnField()
    {
        var status = 503;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] }); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        app.Run(context => { context.Response.StatusCode = status; return Task.CompletedTask; });
        await app.StartAsync();
        var proxy = new SystemUpstreamProxy(() => new RelayConfiguration { DeviceId = new string('e', 64) });
        var probe = new UpstreamProbe(new ProxySettingsTests.AbsentClaudeCode(), proxy, new ProxySettingsTests.AbsentIdentity(), new FixedSiteIdentity(new string('e', 64)));
        var options = new UpstreamOptions { BaseUrl = app.Urls.Single(), AllowPrivateNetwork = true, Preset = UpstreamPreset.Compatible, AuthenticationManual = true };
        var unavailable = await probe.Discover(new UpstreamSnapshot(1, options, "fixture-catalog-secret"), CancellationToken.None);
        Check(unavailable.Status == CatalogStatus.Failed && unavailable.UpstreamStatus == 503 && unavailable.Message.Contains("上游返回 HTTP 503", StringComparison.Ordinal)
            && unavailable.Message.Contains("上游暂时不可用", StringComparison.Ordinal), unavailable.Message);
        status = 418;
        var teapot = await probe.Discover(new UpstreamSnapshot(1, options, "fixture-catalog-secret"), CancellationToken.None);
        Check(teapot.Status == CatalogStatus.Failed && teapot.UpstreamStatus == 418 && teapot.Message.Contains("请检查地址是否填对", StringComparison.Ordinal), teapot.Message);
        status = 404;
        var missing = await probe.Discover(new UpstreamSnapshot(1, options, "fixture-catalog-secret"), CancellationToken.None);
        Check(missing.Status == CatalogStatus.Unsupported && missing.UpstreamStatus is null, missing.Message);
    }

    /// <summary>P-036/P-054：正式服务漏登记任何一个转发依赖，管理宿主在构建容器时就报，不会静默退成「不记录用量」；直接构造时判空当场抛出。</summary>
    public static Task MissingDependencyFailsAtBuild()
    {
        using var home = new TestHome();
        try
        {
            using var app = ManagementWeb.Build(home.Path, 0, services => services.RemoveAll<IClaudeCodeUpdater>());
            Check(false, "A missing updater was silently accepted");
        }
        // 这里是用例期望的拒绝：接住后核对读句或类型，别的异常照常让用例失败。
        catch (AggregateException error) { Check(error.ToString().Contains("IClaudeCodeUpdater", StringComparison.Ordinal), error.Message); }
        try
        {
            _ = new RelayForwarder(new RelayConfiguration { DeviceId = new string('e', 64) }, new Core.Identity.ClientRegistry([]), new Admission(new RelayConfiguration { DeviceId = new string('e', 64) }),
                null!, null!, null!, null!, null!, null!, null!, null!, null!, null!);
            Check(false, "Null dependencies were accepted");
        }
        // 判空是这条用例要的结果：漏传依赖当场抛出。
        catch (ArgumentNullException) { }
        try
        {
            var config = new RelayConfiguration { DeviceId = new string('e', 64) };
            _ = RelayForwarder.Standalone(config, new Core.Identity.ClientRegistry([]), new Admission(config), null!, new Application.Services.Relay.NullRelayEventLog(), new UpstreamResponses(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<RelayForwarder>.Instance);
            Check(false, "A standalone relay without an upstream pool was accepted");
        }
        // 这里是用例期望的拒绝：接住后核对读句或类型，别的异常照常让用例失败。
        catch (ArgumentNullException) { }
        return Task.CompletedTask;
    }

    /// <summary>P-002/P-035：主程序除组合根外不引用 Infrastructure；中转宿主的 Handle 只剩一行转交。</summary>
    public static Task HostDoesNotReachIntoInfrastructure()
    {
        var root = RepositoryRoot();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src", "HouseholdRelay"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                || Path.GetFileName(file) == "CompositionRoot.cs") continue;
            var text = File.ReadAllText(file);
            Check(!text.Contains("Infrastructure.", StringComparison.Ordinal) && !text.Contains("UsageService.", StringComparison.Ordinal)
                && !text.Contains("UpstreamService.", StringComparison.Ordinal), file + " reaches past the application interfaces");
        }
        var server = File.ReadAllLines(Path.Combine(root, "src", "HouseholdRelay", "Relay", "RelayServer.cs"));
        Check(server.Count(line => line.Contains("_forwarder.Handle(", StringComparison.Ordinal)) == 1 && server.Length < 80, "RelayServer must only bind HTTP");
        return Task.CompletedTask;
    }

    /// <summary>仓库根目录：从测试程序目录往上找解决方案文件。</summary>
    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "HouseholdRelay.sln"))) return directory.FullName;
        throw new InvalidOperationException("找不到仓库根目录。");
    }

    /// <summary>P-005/P-022/P-068：161～200 字和带 @ 的模型名配得进上游、存得进单价、筛得出、转发时记得进用量；页面 maxlength 与 Core 一致。</summary>
    public static async Task ModelNameHasOneRule()
    {
        var at = "claude-opus@" + new string('x', 150);
        var long180 = new string('m', 180);
        Check(ModelName.IsValid(at) && ModelName.IsValid(long180) && !ModelName.IsValid(new string('m', 201)) && !ModelName.IsValid("bad model"));
        Check(new UpstreamOptions { BaseUrl = "https://api.example.test", Model = at }.Validated().Model == at, "Upstream options must accept @ and 161-200 characters");
        try { _ = new UpstreamOptions { BaseUrl = "https://api.example.test", Model = "bad model" }.Validated(); Check(false); }
        // 这里是用例期望的拒绝：接住后核对读句或类型，别的异常照常让用例失败。
        catch (InvalidOperationException error) { Check(error.Message == ModelName.Rule, error.Message); }
        new UsageFilter(Model: long180).Validate();
        await using var mock = await UpstreamFixture.Create();
        await using var web = await WebFixture.Create();
        var candidate = await mock.Activate(web.Upstreams);
        await web.Pricing.Save(candidate, long180, "USD", 1, 1, null, null);
        var user = await web.Identities.Create("长模型");
        using (var request = UsageFixture.Request(web, user.Token, body: "{\"model\":\"" + at + "\",\"messages\":[]}"))
        using (var response = await web.Http.SendAsync(request)) Check(response.StatusCode == HttpStatusCode.OK);
        var report = await UsageFixture.Settled(web, 1);
        Check(report.Rows[0].Model == at, "A 162-character model with @ must be recorded: " + report.Rows[0].Model);
        var page = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "wwwroot", "my-usage.html"));
        Check(page.Contains("<input id=\"model\" maxlength=\"" + ModelName.MaxLength + "\">", StringComparison.Ordinal), "my-usage.html maxlength must equal ModelName.MaxLength");
    }

    /// <summary>P-040：对外地址只认 HTTPS 根地址，保存设置与证书用同一个判定。</summary>
    public static Task PublicAddressHasOneRule()
    {
        Check(PublicAddress.IsHttpsRoot("https://relay.example.com:5433/", out var url) && url.Port == 5433);
        foreach (var bad in new[] { "http://relay.example.com/", "https://user:pw@relay.example.com/", "https://relay.example.com/path", "https://relay.example.com/?a=1", "https://relay.example.com/#x", "relay" })
            Check(!PublicAddress.IsHttpsRoot(bad, out _), bad);
        return Task.CompletedTask;
    }

    /// <summary>P-041：这家能不能接请求只有一个结论：Usable 为假的时候 CheckState 一定拒绝，为真的时候一定放行。</summary>
    public static Task ConnectionUsableMatchesCheckState()
    {
        var proxy = new SystemUpstreamProxy(() => new RelayConfiguration { DeviceId = new string('e', 64) });
        var options = new UpstreamOptions { BaseUrl = "https://api.example.test", Mode = UpstreamMode.OAuth, Authentication = AuthenticationScheme.Bearer, OfficialLogin = true };
        foreach (var (credential, available) in new (string?, bool)[] { ("token", true), ("token", false), (null, true) })
        {
            using var connection = new UpstreamConnection(new UpstreamSnapshot(1, options, credential), proxy, 1, () => available);
            var refused = false;
            try { connection.CheckState(); }
            // 拒绝就是这家此刻不能接请求：记下来和 Usable 对照。
            catch (RelayFailure) { refused = true; }
            Check(connection.Usable == !refused && refused == (credential is null || !available), $"credential={credential} available={available}");
        }
        return Task.CompletedTask;
    }

    /// <summary>P-045：40 天、8 人、设置默认值各只定义一处。</summary>
    public static Task LimitsAreDefinedOnce()
    {
        var now = DateTimeOffset.UtcNow;
        Check(UsageRetention.Cutoff(now) == now.AddDays(-UsageRetention.Days) && UsageRetention.Days == 40);
        var clients = Enumerable.Range(0, Core.Identity.User.MaxUsers + 1).Select(index => new Core.Identity.ClientAccess("c" + index, index.ToString("x").PadLeft(64, '0'))).ToArray();
        try { RelayConfiguration.ValidateClients(clients); Check(false, "Nine clients accepted"); }
        // 这里是用例期望的拒绝：接住后核对读句或类型，别的异常照常让用例失败。
        catch (InvalidOperationException error) { Check(error.Message.Contains("最多允许 " + Core.Identity.User.MaxUsers + " 个", StringComparison.Ordinal), error.Message); }
        var defaults = new RelayConfiguration(); var input = new SettingsInput();
        Check(input.ListenPort == defaults.ListenPort && input.MaxConcurrent == defaults.MaxConcurrent && input.MaxPerClient == defaults.MaxPerClient
            && input.MaxQueued == defaults.MaxQueued && input.RequestsPerMinute == defaults.RequestsPerMinute && input.QueueTimeoutSeconds == defaults.QueueTimeoutSeconds
            && input.UploadTimeoutSeconds == defaults.UploadTimeoutSeconds && input.RequestTimeoutSeconds == defaults.RequestTimeoutSeconds
            && input.NormalizeDeviceMetadata == defaults.NormalizeDeviceMetadata && input.UpstreamUserAgent == defaults.UpstreamUserAgent
            && input.ProxyMode == Wire<ProxyMode>.Name(defaults.ProxyMode), "Settings form defaults must come from RelayConfiguration");
        Check(UploadLimits.UpstreamExportBytes == 1024 * 1024 && UploadLimits.OfficialCredentialBytes == 64 * 1024 && UploadLimits.CertificateBytes == 1024 * 1024);
        return Task.CompletedTask;
    }

    /// <summary>P-047：设置项范围只登记一次，校验和设置页都取它；开放路由只登记一次。</summary>
    public static async Task SettingsAndRoutesHaveOneRegistry()
    {
        foreach (var (apply, name) in new (Action<RelayConfiguration, int>, string)[]
        {
            ((config, value) => config.ListenPort = value, nameof(SettingLimits.ListenPort)), ((config, value) => config.MaxConcurrent = value, nameof(SettingLimits.MaxConcurrent)),
            ((config, value) => config.MaxQueued = value, nameof(SettingLimits.MaxQueued)), ((config, value) => config.RequestTimeoutSeconds = value, nameof(SettingLimits.RequestTimeoutSeconds)),
        })
        {
            var range = (SettingRange)typeof(SettingLimits).GetField(name)!.GetValue(null)!;
            var ok = new RelayConfiguration { DeviceId = new string('e', 64) }; apply(ok, range.Max); ok.Validate();
            var bad = new RelayConfiguration { DeviceId = new string('e', 64) }; apply(bad, range.Max + 1);
            try { bad.Validate(); Check(false, name + " above its range was accepted"); }
            // 超出范围必须被拒绝：接住即通过。
            catch (InvalidOperationException) { }
        }
        await using var web = await WebFixture.Create(); await web.Login();
        var html = await web.Http.GetStringAsync(web.Url + "/Settings");
        Check(html.Contains($"min=\"{SettingLimits.MaxQueued.Min}\" max=\"{SettingLimits.MaxQueued.Max}\"", StringComparison.Ordinal), "Settings page must render the registry range");
        Check(RelayRoutes.Exposed.Count == 3 && RelayRoutes.Kind(RelayRoutes.Models) == UsageKind.Models && UpstreamAddress.Endpoint(new UpstreamOptions { BaseUrl = "https://api.example.test" }, RelayRoutes.CountTokens).AbsolutePath.EndsWith("/count_tokens", StringComparison.Ordinal));
    }

    /// <summary>P-048/P-052/P-119/P-038：报告带显示名表与分页结论，个人页脚本与管理页只读它；静态个人页的下拉框不再手抄，只留「全部」，其余选项由脚本按 /my-usage/labels（Core 那一份）填。</summary>
    public static Task ReportCarriesLabelsAndPaging()
    {
        UsageReport Report(int page, long total) => new(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, page, UsageFilter.PageSize, total, [], [], 0);
        Check(Report(1, 0) is { TotalPages: 0, HasPrevious: false, HasNext: false } && Report(1, 25) is { TotalPages: 1, HasNext: false }
            && Report(1, 26) is { TotalPages: 2, HasNext: true } && Report(2, 26) is { HasPrevious: true, HasNext: false });
        var json = JsonSerializer.Serialize(Report(1, 26), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var parsed = JsonDocument.Parse(json); var root = parsed.RootElement; var labels = root.GetProperty("labels");
        Check(root.GetProperty("hasNext").GetBoolean() && labels.GetProperty("completeness").GetProperty("not_provided").GetString() == "上游未提供"
            && labels.GetProperty("outcomes").GetProperty("started").GetString() == "处理中 / 待写入"
            && json.IndexOf("\"periods\"", StringComparison.Ordinal) < json.IndexOf("\"labels\"", StringComparison.Ordinal), json);
        var page = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "wwwroot", "my-usage.html"));
        Check(page.Contains("<select id=\"kind\"><option value=\"\">全部接口</option></select>", StringComparison.Ordinal)
            && page.Contains("<select id=\"outcome\"><option value=\"\">全部结果</option></select>", StringComparison.Ordinal),
            "my-usage.html must not copy the option labels by hand");
        var script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "wwwroot", "usage-personal.js"));
        Check(!script.Contains("'推理'", StringComparison.Ordinal) && !script.Contains("'美元'", StringComparison.Ordinal) && script.Contains("report.hasNext", StringComparison.Ordinal)
            && script.Contains("'/my-usage/labels'", StringComparison.Ordinal), "The personal script must read labels, currencies and paging from the server");
        return Task.CompletedTask;
    }

    /// <summary>P-115：切换的两个入口各自写清版本核对；不给版本时按候选保存时的版本核对，给了不对的版本就拒绝。</summary>
    public static async Task ActivateOverloadsCheckVersions()
    {
        await using var mock = await UpstreamFixture.Create();
        using var home = new TestHome(); var process = new TestProcess(home.Path);
        var version = (await process.Upstreams.ReadActive()).ActiveVersion;
        var id = await process.Upstreams.SaveCandidate(version, mock.Options(), "fixture-overload-secret");
        Check((await process.Upstreams.TestCandidate(id, true, CancellationToken.None)).CanActivate);
        try { await process.Upstreams.Activate(id, version + 7); Check(false, "A stale explicit version was accepted"); }
        // 这里是用例期望的拒绝：接住后核对读句或类型，别的异常照常让用例失败。
        catch (InvalidOperationException) { }
        await process.Upstreams.Activate(id);
        Check((await process.Upstreams.ReadActive()).UpstreamCandidateId == id);
    }

    /// <summary>P-049：命令行退出码按类别固定：未知子命令是参数拒绝（1），数据目录不能用是本机环境（3）。</summary>
    public static async Task ExitCodesByCategory()
    {
        Check(await HouseholdRelay.Program.Main(["no-such-command"]) == ExitCodes.Rejected);
        using var home = new TestHome();
        var file = Path.Combine(home.Path, "not-a-folder");
        await File.WriteAllTextAsync(file, "x");
        var code = await HouseholdRelay.Program.Main(["clients", "list", "--home", file]);
        Check(code == ExitCodes.Environment, "exit=" + code);
    }
}
