using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Core.Upstream;
using Infrastructure.ExternalSources.ClaudeCode;
using static Tests.TestSuite;

namespace Tests;

/// <summary>
/// 上游导出与导入：两套临时实例之间经网页导出、导入，核对顺序、未测试、登录文件逐字节相同、密钥不出现在文件页面日志里；
/// 密码错、文件被改、格式版本不认识各自读出原因且一条不写；官方账户导出即搬走，之后不转发、不续期；v7 升级失败回滚。
/// </summary>
static class UpstreamTransferTests
{
    private const string KeyA = "fixture-transfer-key-a-0123456789";
    private const string KeyB = "fixture-transfer-key-b-0123456789";
    private const string OfficialToken = "fixture-transfer-official-token";
    private const string Password = "fixture-export-password-1";

    /// <summary>
    /// 往返：旧实例存两家 API 上游（甲、乙）加一家导入登录文件得到的官方账户，经网页全选导出；新实例里已有一家「原有」，经网页导入后
    /// 三家按原顺序排在「原有」后面、都是未测试，官方账户的登录文件逐字节相同；导入的甲测试时带的正是原密钥。
    /// 导出文件、两边的页面与事件日志里都找不到任何一个原始密钥或令牌。
    /// </summary>
    public static async Task RoundTrip()
    {
        using var claude = new FakeClaudeLauncher("success");
        await using var mockA = await UpstreamFixture.Create(); await using var mockB = await UpstreamFixture.Create();
        var logs = new ConcurrentQueue<string>();
        await using var source = await Web(claude, logs); await using var target = await Web(claude, logs);
        var login = FakeClaude.Credential(OfficialToken, 60);
        var ids = new[]
        {
            source.Upstreams.SaveCandidate(source.Upstreams.ReadActive().Version, mockA.Options(), KeyA, "甲"),
            source.Upstreams.SaveCandidate(source.Upstreams.ReadActive().Version, mockB.Options(), KeyB, "乙"),
            await source.Upstreams.ImportOfficial(source.Upstreams.ReadActive().Version, Official(mockA), login, "官方", CancellationToken.None)
        };
        await source.Login();
        var exported = await Export(source, ids, Password);
        Check(exported.Html.Contains("已导出 3 家上游：「甲」「乙」「官方」。浏览器正在下载") && exported.Html.Contains("官方账户「官方」已经搬走：本机不再用它转发、不再续期。")
            && exported.Html.Contains("下一步：把文件拷到新电脑"), exported.Html);
        Check(source.Upstreams.ListCandidates().Single(item => item.Name == "官方").ExportedAt is not null, "Exporting moves the official account");

        target.Upstreams.SaveCandidate(target.Upstreams.ReadActive().Version, mockA.Options(), "fixture-transfer-existing-key", "原有");
        await target.Login();
        var html = await Import(target, exported.File, Password);
        Check(html.Contains("已导入 3 家上游：「甲」「乙」「官方」，都排在已有上游后面，尚未测试。下一步：逐家按「选择模型并测试」"), html);
        var saved = target.Upstreams.ListCandidates();
        Check(saved.Select(item => item.Name).SequenceEqual(["原有", "甲", "乙", "官方"]), string.Join(",", saved.Select(item => item.Name)));
        Check(saved.Skip(1).All(item => item.State == "untested" && item.Report is null && item.ExportedAt is null), "Imported upstreams start untested");
        var official = saved.Single(item => item.Name == "官方");
        Check(official.Options is { OfficialLogin: true, Mode: "oauth" } && File.ReadAllBytes(Path.Combine(target.Home.Path, ".state", "official-accounts", official.Id, OfficialCredential.FileName)).SequenceEqual(login),
            "The login file must arrive byte for byte");
        var imported = saved.Single(item => item.Name == "甲"); mockA.Calls.Clear();
        Check((await target.Upstreams.TestCandidate(imported.Id, true, CancellationToken.None)).CanActivate && mockA.Calls.Last().Headers["x-api-key"] == KeyA, "The imported key is the original key");

        foreach (var secret in new[] { KeyA, KeyB, OfficialToken, "fixture-refresh-token" })
        {
            Check(!Contains(exported.File, secret) && !exported.Html.Contains(secret) && !html.Contains(secret), "Secrets must not appear in the file or pages: " + secret);
            Check(!logs.Any(line => line.Contains(secret)), "Secrets must not appear in event logs: " + secret);
        }
    }

    /// <summary>
    /// 导入被拒：密码错、密文改一个字节、格式版本号不认识、根本不是导出文件，各自读出原因与下一步；每种情况新实例里一家上游、一个账户目录都没多。
    /// </summary>
    public static async Task ImportRefusals()
    {
        using var claude = new FakeClaudeLauncher("success");
        await using var mock = await UpstreamFixture.Create();
        await using var source = await Web(claude); await using var target = await Web(claude);
        var ids = new[]
        {
            source.Upstreams.SaveCandidate(source.Upstreams.ReadActive().Version, mock.Options(), KeyA, "甲"),
            await source.Upstreams.ImportOfficial(source.Upstreams.ReadActive().Version, Official(mock), FakeClaude.Credential(OfficialToken, 60), "官方", CancellationToken.None)
        };
        var file = source.Upstreams.Export(ids, Password).File;
        await target.Login();
        var tampered = (byte[])file.Clone(); tampered[^1] ^= 1;
        var future = (byte[])file.Clone(); future[10] = 9;
        foreach (var (content, password, expected) in new[]
        {
            (file, "fixture-wrong-password", "导出密码不对。请输入导出时设的那个密码再试"),
            (tampered, Password, "导出文件被改过或不完整：密码是对的，但内容校验没有通过。请回旧电脑重新导出一份"),
            (future, Password, "这个导出文件的格式版本是 9，本程序只认版本 1。"),
            (Encoding.UTF8.GetBytes("not an export " + KeyA), Password, "选的文件不是上游导出文件。")
        })
        {
            var html = await Import(target, content, password);
            Check(html.Contains(expected) && !html.Contains(KeyA), expected + " / " + html);
            Check(target.Upstreams.ListCandidates().Count == 0 && NoAccountDirectories(target), "Refused imports must write nothing: " + expected);
        }
    }

    /// <summary>
    /// 导出被拒：两次密码不一样、密码不足 12 个字符、一家都没勾、没带防 CSRF 令牌，各自读出原因或被拦下；官方账户都没有被记成搬走，页面不出现下载链接。
    /// </summary>
    public static async Task ExportRefusals()
    {
        using var claude = new FakeClaudeLauncher("success");
        await using var mock = await UpstreamFixture.Create();
        await using var web = await Web(claude);
        var id = await web.Upstreams.ImportOfficial(web.Upstreams.ReadActive().Version, Official(mock), FakeClaude.Credential(OfficialToken, 60), "官方", CancellationToken.None);
        await web.Login();
        foreach (var (ids, password, again, expected) in new[]
        {
            (new[] { id }, Password, Password + "x", "两次输入的导出密码不一样，请重新输入两遍"),
            (new[] { id }, "short-pass", "short-pass", "导出密码需要 12 至 128 个字符。"),
            (Array.Empty<string>(), Password, Password, "请至少勾选一家要导出的上游")
        })
        {
            var html = WebUtility.HtmlDecode(await Post(web, ids, password, again, await web.Csrf("/Upstream")));
            Check(html.Contains(expected) && !html.Contains("data:application/octet-stream"), expected + " / " + html);
        }
        using (var request = ExportRequest(web, [id], Password, Password, null))
        using (var response = await web.Http.SendAsync(request)) Check(response.StatusCode == HttpStatusCode.BadRequest, "Export requires the antiforgery token");
        Check(web.Upstreams.ReadCandidate(id).ExportedAt is null, "Refused exports must not move the account");
    }

    /// <summary>
    /// 导出即搬走：在用的是官方账户（令牌 2 分钟后到期，任何一次读令牌都会触发续期），备用是测试通过的 API 上游「乙」。
    /// 导出前页面事先读出「自动切换关着，导出后中转会没有可用上游」。导出后：开关关着时请求得到 503、官方那边收到 0 次；打开开关后由乙接住；
    /// 获取模型、测试、切换回它都被拒；上游页与概览读出「已经导出搬走」与下一步；整个过程假 Claude Code 一次续期都没跑，账户目录还在。
    /// </summary>
    public static async Task MovedAccountStops()
    {
        using var claude = new FakeClaudeLauncher("success");
        await using var mockOfficial = await UpstreamFixture.Create(); await using var mockB = await UpstreamFixture.Create();
        await using var web = await Web(claude);
        var service = web.Upstreams;
        var official = await service.ImportOfficial(service.ReadActive().Version, Official(mockOfficial), FakeClaude.Credential(OfficialToken, 60), "官方", CancellationToken.None);
        service.ChooseModel(official, "fixture-model");
        Check((await service.TestCandidate(official, true, CancellationToken.None)).CanActivate, "Official upstream passes");
        var standby = service.SaveCandidate(service.ReadActive().Version, mockB.Options(), KeyB, "乙");
        Check((await service.TestCandidate(standby, true, CancellationToken.None)).CanActivate, "Standby passes");
        service.Activate(official); service.SetFailover(false);
        var directory = Path.Combine(web.Home.Path, ".state", "official-accounts", official);
        FakeClaude.Write(directory, OfficialToken, 2);
        var token = web.Identities.Create("搬走").Token;
        await web.Login();
        var before = WebUtility.HtmlDecode(await web.Http.GetStringAsync(web.Url + "/Upstream"));
        Check(before.Contains("在用的「官方」是登录得到的官方账户，勾选它导出后它就搬走了：自动切换关着，导出后中转会没有可用上游。"), "The page warns before exporting the active account");
        Check(!File.ReadAllText(Path.Combine(directory, "fake-calls.log")).Contains("-p /usage"), "No refresh has run before the export");

        await Export(web, [official], Password);
        mockOfficial.Calls.Clear(); mockB.Calls.Clear();
        using (var refused = await UpstreamFixture.RelayRequest(web, token)) Check(refused.StatusCode == HttpStatusCode.ServiceUnavailable, $"Switch off: {refused.StatusCode}");
        service.SetFailover(true);
        using (var served = await UpstreamFixture.RelayRequest(web, token)) Check(served.StatusCode == HttpStatusCode.OK, $"Switch on: {served.StatusCode}");
        Check(mockOfficial.Calls.IsEmpty && mockB.Calls.Count == 1, $"official={mockOfficial.Calls.Count}, B={mockB.Calls.Count}");
        foreach (var attempt in new Func<Task>[]
        {
            () => service.DiscoverModels(official, CancellationToken.None), () => service.TestCandidate(official, true, CancellationToken.None),
            () => { service.Activate(official); return Task.CompletedTask; }
        })
        {
            try { await attempt(); Check(false, "A moved account must be refused"); }
            // 预期的拒绝：读出「已经导出搬走」，不读令牌、不起续期。
            catch (InvalidOperationException error) { Check(error.Message.Contains("已经导出搬走"), error.Message); }
        }
        var expected = "在用的「官方」已经导出搬走，本机不再用它转发、不再续期。自动切换开着：请求按「已保存的上游」的顺序交给其他测试通过的上游。";
        foreach (var path in new[] { "/Upstream", "/" })
        {
            var html = WebUtility.HtmlDecode(await web.Http.GetStringAsync(web.Url + path));
            Check(html.Contains(expected) && html.Contains("已导出搬走"), path + " must read the moved account and the next step");
        }
        Check(!File.ReadAllText(Path.Combine(directory, "fake-calls.log")).Contains("-p /usage") && File.Exists(Path.Combine(directory, OfficialCredential.FileName)),
            "A moved account is never refreshed and its directory stays until deleted");
    }

    /// <summary>v6 → v7：检查点在加列之后、写版本号之前失败时整个升级回滚（版本仍是 6、列不在），重开后升到 7 且已有上游都读作没导出过。</summary>
    public static Task SchemaUpgrade()
    {
        using var home = new TestHome(); var process = new TestProcess(home.Path);
        process.Upstreams.SaveCandidate(process.Upstreams.ReadActive().Version, new UpstreamOptions { BaseUrl = "https://example.com" }, KeyA, "旧上游");
        process.Database.Write((connection, transaction) => RelayDatabase.Execute(connection, transaction, "ALTER TABLE upstream_candidates DROP COLUMN exported_at; PRAGMA user_version=6;"));
        var rejected = false;
        try { _ = new TestProcess(home.Path, migrationCheckpoint: () => throw new IOException("fixture-checkpoint")).Database; }
        catch (IOException error) when (error.Message == "fixture-checkpoint") { /* 检查点在加列之后、写版本号之前失败，整个升级应回滚；随后核对版本号与列。 */ rejected = true; }
        Check(rejected, "Checkpoint failure must abort the upgrade");
        int Count(RelayDatabase database, string sql) { using var connection = database.Open(); return Convert.ToInt32(RelayDatabase.Scalar(connection, null, sql)); }
        const string Column = "SELECT COUNT(*) FROM pragma_table_info('upstream_candidates') WHERE name='exported_at'";
        Check(Count(process.Database, "PRAGMA user_version") == 6 && Count(process.Database, Column) == 0, "Rollback keeps version 6 without the column");
        var upgraded = new TestProcess(home.Path);
        Check(Count(upgraded.Database, "PRAGMA user_version") == 7 && Count(upgraded.Database, Column) == 1);
        Check(upgraded.Upstreams.ListCandidates() is [{ Name: "旧上游", ExportedAt: null }], "Existing upstreams read as never exported");
        return Task.CompletedTask;
    }

    /// <summary>带假 Claude Code 的管理网页主机；给了 <paramref name="logs"/> 时事件日志收进它。</summary>
    private static Task<WebFixture> Web(FakeClaudeLauncher claude, ConcurrentQueue<string>? logs = null) => WebFixture.Create(overrides: services =>
    {
        claude.Register(services);
        if (logs is not null) services.AddSingleton<IRelayEventLog>(new FixtureLog(logs));
    });

    /// <summary>指向模拟上游的官方账户配置：只给地址与私网开关，账户模式与 Bearer 由服务定死。</summary>
    private static UpstreamOptions Official(UpstreamFixture mock) => new() { BaseUrl = mock.Url, AllowPrivateNetwork = true, Model = "fixture-model" };

    /// <summary>经网页导出 <paramref name="ids"/>，要求成功，取出页面（已解码）与自动下载链接里的加密文件。</summary>
    private static async Task<(string Html, byte[] File)> Export(WebFixture web, IEnumerable<string> ids, string password)
    {
        var html = WebUtility.HtmlDecode(await Post(web, ids, password, password, await web.Csrf("/Upstream")));
        var link = Regex.Match(html, "href=\"data:application/octet-stream;base64,([^\"]+)\" download=\"(household-relay-upstreams-[0-9-]+\\.hrupstreams)\" data-auto-download");
        Check(link.Success, "The export page must carry the download link: " + html);
        return (html, Convert.FromBase64String(link.Groups[1].Value));
    }

    /// <summary>提交导出表单（勾选可重复），返回页面原文。</summary>
    private static async Task<string> Post(WebFixture web, IEnumerable<string> ids, string password, string again, string csrf)
    {
        using var request = ExportRequest(web, ids, password, again, csrf);
        using var response = await web.Http.SendAsync(request);
        Check(response.StatusCode == HttpStatusCode.OK, "Export page: " + response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>组装导出表单请求：每个勾选一条 exportIds，带本站 Origin；<paramref name="csrf"/> 为空时不带防伪令牌。</summary>
    private static HttpRequestMessage ExportRequest(WebFixture web, IEnumerable<string> ids, string password, string again, string? csrf)
    {
        var values = ids.Select(id => KeyValuePair.Create("exportIds", id)).Append(KeyValuePair.Create("exportPassword", password)).Append(KeyValuePair.Create("exportPasswordAgain", again));
        if (csrf is not null) values = values.Append(KeyValuePair.Create("__RequestVerificationToken", csrf));
        var request = new HttpRequestMessage(HttpMethod.Post, web.Url + "/Upstream?handler=Export") { Content = new FormUrlEncodedContent(values) };
        request.Headers.TryAddWithoutValidation("Origin", web.Url);
        return request;
    }

    /// <summary>经网页导入 <paramref name="file"/>，返回已解码的页面。</summary>
    private static async Task<string> Import(WebFixture web, byte[] file, string password)
    {
        using var page = await web.Post("/Upstream?handler=ImportUpstreams", new()
        {
            ["importPassword"] = password, ["Version"] = web.Upstreams.ReadActive().Version.ToString(), ["__RequestVerificationToken"] = await web.Csrf("/Upstream")
        }, "UpstreamFile", "upstreams.hrupstreams", file);
        Check(page.StatusCode == HttpStatusCode.OK, "Import page: " + page.StatusCode);
        return WebUtility.HtmlDecode(await page.Content.ReadAsStringAsync());
    }

    /// <summary>字节里有没有 <paramref name="secret"/> 的 UTF-8 原文。</summary>
    private static bool Contains(byte[] content, string secret) => content.AsSpan().IndexOf(Encoding.UTF8.GetBytes(secret)) >= 0;

    /// <summary>数据目录下没有任何官方账户目录（目录本身不存在或为空）。</summary>
    private static bool NoAccountDirectories(WebFixture web)
    {
        var root = Path.Combine(web.Home.Path, ".state", "official-accounts");
        return !Directory.Exists(root) || Directory.GetDirectories(root).Length == 0;
    }
}
