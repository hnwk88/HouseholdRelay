using System.Net;
using System.Text.Json;
using HouseholdRelay;
using Microsoft.AspNetCore.Http;
using static Tests.TestSuite;

namespace Tests;

/// <summary>只填名称、地址、密钥时的自动探测，手动指定的优先级，按名称切换与改名，以及秘密不出现在页面和错误里。</summary>
static class UpstreamDetectionTests
{
    /// <summary>
    /// 验证认证方式探测的五种上游：只认 x-api-key、只认 Bearer、只认 Bearer 且对 x-api-key 回 400（照 MyCoAI 视同拒绝、改试 Bearer）、两种都拒、不提供模型目录。
    /// 获取模型读出被接受的方式或拒绝原因；测试按目录接受的方式开始，目录缺失时 x-api-key 被拒再换 Bearer；探测结果写回该上游；两种都拒时不发任何模型调用。
    /// 没有目录时页面把模型下拉框换成文本框。
    /// </summary>
    public static async Task AuthenticationDetection()
    {
        await using var mock = await UpstreamFixture.Create(); await using var web = await WebFixture.Create(); await web.Login();
        var service = web.Upstreams;
        string? accepted = null; var catalog = true; var badRequest = false;
        mock.Handler = async context =>
        {
            var headers = mock.Calls.Last().Headers;
            var ok = accepted == "x-api-key" ? headers.GetValueOrDefault("x-api-key") == "fixture-detect-secret"
                : accepted == "bearer" && headers.GetValueOrDefault("Authorization") == "Bearer fixture-detect-secret";
            if (context.Request.Path.Value!.EndsWith("/models", StringComparison.Ordinal) && !catalog) { context.Response.StatusCode = 404; return; }
            if (!ok && badRequest) { context.Response.StatusCode = 400; return; }
            if (!ok) { context.Response.StatusCode = 401; await context.Response.WriteAsJsonAsync(new { type = "error", error = new { type = "authentication_error" } }); return; }
            await UpstreamFixture.Success(context);
        };
        async Task<(ModelCatalog Catalog, UpstreamReport Report, UpstreamCandidate Candidate)> Detect(string name)
        {
            var id = service.SaveCandidate(service.ReadActive().Version, new UpstreamOptions { BaseUrl = mock.Url, AllowPrivateNetwork = true }, "fixture-detect-secret", name);
            var found = await service.DiscoverModels(id, CancellationToken.None);
            service.ChooseModel(id, "fixture-model");
            var report = await service.TestCandidate(id, true, CancellationToken.None);
            return (found, report, service.ReadCandidate(id));
        }
        accepted = "x-api-key";
        var key = await Detect("只认密钥头");
        Check(key.Catalog.Status == "passed" && key.Catalog.Authentication == "x-api-key" && key.Catalog.Message.StartsWith("已获取 1 个模型，认证用 x-api-key", StringComparison.Ordinal));
        Check(key.Report.CanActivate && key.Report.Message.StartsWith("认证用 x-api-key，按标准 Messages 方式通过，模型 fixture-model。", StringComparison.Ordinal) && key.Candidate.Options.Authentication == "x-api-key");
        accepted = "bearer";
        var bearer = await Detect("只认 Bearer");
        Check(bearer.Catalog.Authentication == "bearer" && bearer.Report.CanActivate && bearer.Report.Message.StartsWith("认证用 Bearer，按标准 Messages", StringComparison.Ordinal));
        Check(bearer.Candidate.Options.Authentication == "bearer" && !bearer.Candidate.Options.AuthenticationManual);
        badRequest = true;
        var missingHeader = await Detect("缺头回 400");
        Check(missingHeader.Catalog.Authentication == "bearer" && missingHeader.Report.CanActivate && missingHeader.Candidate.Options.Authentication == "bearer", missingHeader.Catalog.Message);
        badRequest = false;
        accepted = null; var before = mock.Calls.Count(call => call.Path.Contains("/messages"));
        var rejected = await Detect("两种都拒");
        Check(rejected.Catalog.Status == "rejected" && rejected.Catalog.Message.Contains("两种认证方式（x-api-key、Bearer）都试过"));
        Check(!rejected.Report.CanActivate && rejected.Report.Authentication == "reconnect" && rejected.Report.Message.Contains("两种认证方式"));
        Check(mock.Calls.Count(call => call.Path.Contains("/messages")) == before, "Rejected keys must not reach a billable model call");
        accepted = "bearer"; catalog = false;
        var absent = await Detect("没有目录");
        Check(absent.Catalog.Status == "unsupported" && absent.Catalog.Message == "上游不提供模型目录，请手填模型名。" && absent.Catalog.Authentication is null);
        Check(absent.Report.CanActivate && absent.Report.ModelDiscovery == "unsupported" && absent.Candidate.Options.Authentication == "bearer");
        using var page = await web.Http.GetAsync(web.Url + "/Upstream?candidateId=" + absent.Candidate.Id);
        var html = WebUtility.HtmlDecode(await page.Content.ReadAsStringAsync());
        Check(html.Contains("模型名称（上游没有提供模型目录，请手填）") && html.Contains("<input id=\"test-model\" name=\"model\" value=\"fixture-model\""), "No catalog must offer a text box");
    }

    /// <summary>
    /// 验证手动指定优先于自动探测：上游只认 Bearer，高级设置手动指定 x-api-key 时获取模型读出「手动指定为 x-api-key」的拒绝原因，测试失败后配置仍是手动的 x-api-key、不被改成 Bearer；
    /// 手动指定 Bearer 时通过，读句注明「（手动指定）」。
    /// </summary>
    public static async Task ManualOverride()
    {
        await using var mock = await UpstreamFixture.Create(); using var home = new TestHome(); var service = new TestProcess(home.Path).Upstreams;
        mock.Handler = context => mock.Calls.Last().Headers.GetValueOrDefault("Authorization") == "Bearer fixture-manual-secret"
            ? UpstreamFixture.Success(context) : Status(context, 401);
        var options = new UpstreamOptions { BaseUrl = mock.Url, AllowPrivateNetwork = true, Model = "fixture-model", Authentication = "x-api-key", AuthenticationManual = true };
        var forced = service.SaveCandidate(service.ReadActive().Version, options, "fixture-manual-secret", "手动密钥头");
        var catalog = await service.DiscoverModels(forced, CancellationToken.None);
        Check(catalog.Status == "rejected" && catalog.Message.Contains("手动指定为 x-api-key"), catalog.Message);
        var report = await service.TestCandidate(forced, true, CancellationToken.None);
        var kept = service.ReadCandidate(forced).Options;
        Check(!report.CanActivate && kept.Authentication == "x-api-key" && kept.AuthenticationManual, "Detection must never overwrite a manual choice");
        Check(mock.Calls.All(call => !call.Headers.ContainsKey("Authorization")), "A manual x-api-key choice must not silently try Bearer");
        var chosen = service.SaveCandidate(service.ReadActive().Version, options with { Authentication = "bearer" }, "fixture-manual-secret", "手动 Bearer");
        var passed = await service.TestCandidate(chosen, true, CancellationToken.None);
        Check(passed.CanActivate && passed.Message.StartsWith("认证用 Bearer（手动指定），按标准 Messages 方式通过", StringComparison.Ordinal), passed.Message);
    }

    /// <summary>
    /// 验证名称与按主键关联：两个具名上游测通过后，经网页按名称切换（核对页面看到的版本，过期版本被拒）；切回最先激活的那个不受它保存时版本的限制；
    /// 改名只改显示，活动版本号、候选主键和用量明细里的上游版本都不变，概览页与连接说明页读出新名称。
    /// </summary>
    public static async Task NamesAndSwitching()
    {
        await using var first = await UpstreamFixture.Create(); await using var second = await UpstreamFixture.Create(); await using var web = await WebFixture.Create(); await web.Login();
        var service = web.Upstreams; var person = web.Identities.Create("切换测试");
        var a = await Passed(service, first, "甲家"); var b = await Passed(service, second, "乙家");
        await Switch(web, a, "甲家"); using (var call = await UpstreamFixture.RelayRequest(web, person.Token)) Check(call.IsSuccessStatusCode);
        var stale = service.ReadActive().Version;
        await Switch(web, b, "乙家"); Check(second.Calls.Count(call => call.Path.Contains("/messages")) == 1);
        var expired = new Dictionary<string, string> { ["candidateId"] = a, ["Version"] = stale.ToString(), ["__RequestVerificationToken"] = await web.Csrf("/Upstream") };
        using (var refused = await web.Post("/Upstream?handler=Activate", expired))
            Check(WebUtility.HtmlDecode(await refused.Content.ReadAsStringAsync()).Contains("活动上游已被另一页面或命令更换") && service.ReadActive().CandidateId == b);
        await Switch(web, a, "甲家");
        using (var call = await UpstreamFixture.RelayRequest(web, person.Token, "after-switch-back")) Check(call.IsSuccessStatusCode && first.Calls.Last().Body.Contains("after-switch-back"));
        var active = service.ReadActive(); var history = web.App.Services.GetRequiredService<IUsageService>().Query(new()).Rows.Select(row => (row.RequestId, row.UpstreamVersion)).ToArray();
        var rename = new Dictionary<string, string> { ["candidateId"] = a, ["newName"] = "公司甲家", ["__RequestVerificationToken"] = await web.Csrf("/Upstream") };
        using (var renamed = await web.Post("/Upstream?handler=Rename", rename)) Check(WebUtility.HtmlDecode(await renamed.Content.ReadAsStringAsync()).Contains("已改名为「公司甲家」"));
        var after = service.ReadActive();
        Check(after.Version == active.Version && after.CandidateId == a && after.Name == "公司甲家");
        Check(web.App.Services.GetRequiredService<IUsageService>().Query(new()).Rows.Select(row => (row.RequestId, row.UpstreamVersion)).SequenceEqual(history), "Renaming must not touch usage history");
        foreach (var path in new[] { "/", "/Connection" })
        {
            using var page = await web.Http.GetAsync(web.Url + path);
            Check(WebUtility.HtmlDecode(await page.Content.ReadAsStringAsync()).Contains("公司甲家"), path + " must read the active name");
        }
    }

    /// <summary>
    /// 验证秘密不出现在页面与错误里：上游在 401 正文和响应头里回显密钥，获取模型、测试、列表页面、报告 JSON 和读句都不含它；
    /// 把密钥写进名称时拒绝，错误文字也不复述密钥。
    /// </summary>
    public static async Task SecretsNeverShown()
    {
        await using var mock = await UpstreamFixture.Create(); await using var web = await WebFixture.Create(); await web.Login();
        const string secret = "fixture-echoed-secret-value";
        mock.Handler = async context =>
        {
            context.Response.StatusCode = 401; context.Response.Headers["x-echo"] = secret;
            await context.Response.WriteAsJsonAsync(new { type = "error", error = new { type = "authentication_error", message = "invalid key " + secret } });
        };
        var values = new Dictionary<string, string>
        {
            ["Api.Name"] = "回显测试", ["Api.BaseUrl"] = mock.Url, ["Api.Credential"] = secret, ["Api.AllowPrivateNetwork"] = "true",
            ["Version"] = web.Upstreams.ReadActive().Version.ToString(), ["__RequestVerificationToken"] = await web.Csrf("/Upstream")
        };
        using var discovered = await web.Post("/Upstream?handler=DiscoverApi", values);
        var html = await discovered.Content.ReadAsStringAsync();
        Check(discovered.IsSuccessStatusCode && WebUtility.HtmlDecode(html).Contains("密钥被上游拒绝") && !html.Contains(secret));
        var candidate = web.Upstreams.LatestCandidate()!;
        var test = new Dictionary<string, string> { ["candidateId"] = candidate.Id, ["model"] = "fixture-model", ["__RequestVerificationToken"] = await web.Csrf("/Upstream") };
        using var tested = await web.Post("/Upstream?handler=Test", test);
        Check(!(await tested.Content.ReadAsStringAsync()).Contains(secret));
        using var listed = await web.Http.GetAsync(web.Url + "/Upstream");
        Check(!(await listed.Content.ReadAsStringAsync()).Contains(secret));
        Check(!JsonSerializer.Serialize(web.Upstreams.ListCandidates()).Contains(secret));
        var leaked = new Dictionary<string, string>(values) { ["Api.Name"] = "名称里有" + secret, ["__RequestVerificationToken"] = await web.Csrf("/Upstream") };
        using var refused = await web.Post("/Upstream?handler=DiscoverApi", leaked);
        var refusal = await refused.Content.ReadAsStringAsync();
        Check(WebUtility.HtmlDecode(refusal).Contains("凭据只能填写在密钥框里") && !refusal.Contains(secret) && web.Upstreams.ListCandidates().Count == 1);
    }

    /// <summary>
    /// 验证新实例的上游页（含命令行 init 生成证书之后）：库里的活动行是旧默认的账户模式，但页面读出「还没有上游」，第一张表单是「API 供应商」、排在「官方账户」之前，
    /// 官方账户块直接给出「登录官方账户」按钮；全局代理默认不使用，测试按钮写明费用、没有单独的同意复选框。
    /// </summary>
    public static async Task NewInstanceDefaults()
    {
        await using var web = await WebFixture.Create(); await web.Login();
        web.Settings.ConfigureLegacyTls("localhost", 5433, 8787);
        using var page = await web.Http.GetAsync(web.Url + "/Upstream");
        var html = WebUtility.HtmlDecode(await page.Content.ReadAsStringAsync());
        var api = html.IndexOf("id=\"api-heading\"", StringComparison.Ordinal); var account = html.IndexOf("id=\"account-heading\"", StringComparison.Ordinal);
        var login = html.IndexOf("handler=LoginAccount", StringComparison.Ordinal);
        Check(api > 0 && account > api && html.Contains("还没有上游") && login > account && html.Contains("登录官方账户</button>") && !html.Contains("登录功能将在下一版加入"));
        var discoverApi = html.IndexOf("handler=DiscoverApi", StringComparison.Ordinal);
        Check(discoverApi > api && discoverApi < html.IndexOf("handler=DiscoverAccount", StringComparison.Ordinal) && !html.Contains("handler=Test"), "The first upstream form must be the API supplier");
        Check(!html.Contains("name=\"confirmed\"") && web.Settings.Read().Value.ProxyMode == "none");
    }

    /// <summary>保存一个指向模拟上游的具名上游（自动探测），获取模型、选模型、测试通过后返回主键。</summary>
    private static async Task<string> Passed(IUpstreamService service, UpstreamFixture mock, string name)
    {
        var id = service.SaveCandidate(service.ReadActive().Version, new UpstreamOptions { BaseUrl = mock.Url, AllowPrivateNetwork = true }, "fixture-" + mock.Url.Length + "-secret", name);
        await service.DiscoverModels(id, CancellationToken.None); service.ChooseModel(id, "fixture-model");
        Check((await service.TestCandidate(id, true, CancellationToken.None)).CanActivate);
        return id;
    }

    /// <summary>经网页的「切换到」按钮切换，带上页面看到的活动版本，并核对读句与活动上游。</summary>
    private static async Task Switch(WebFixture web, string id, string name)
    {
        var values = new Dictionary<string, string> { ["candidateId"] = id, ["Version"] = web.Upstreams.ReadActive().Version.ToString(), ["__RequestVerificationToken"] = await web.Csrf("/Upstream") };
        using var response = await web.Post("/Upstream?handler=Activate", values);
        Check(WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()).Contains($"已切换到「{name}」") && web.Upstreams.ReadActive().CandidateId == id);
    }

    /// <summary>只回状态码、不带正文的模拟应答。</summary>
    private static Task Status(HttpContext context, int status) { context.Response.StatusCode = status; return Task.CompletedTask; }
}
