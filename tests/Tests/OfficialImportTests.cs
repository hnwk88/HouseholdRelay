using System.Net;
using System.Text;
using System.Text.Json;
using Core.Upstream;
using Infrastructure.ExternalSources.ClaudeCode;
using static Tests.TestSuite;

namespace Tests;

/// <summary>「导入登录文件」：用假 Claude Code 验证导入后的上游与登录得到的一样（邮箱命名、原样保存、到期刷新），以及每种不合格文件读出原因且不留痕迹。</summary>
static class OfficialImportTests
{
    /// <summary>导入文件里的访问令牌；测试断言它不出现在页面、库和错误里。</summary>
    private const string ImportedToken = "fixture-imported-token";

    /// <summary>
    /// 网页导入一份合格的 <c>.credentials.json</c>（名称留空）：名称取登录邮箱，文件原样放进以上游主键命名的账户目录，只跑了 <c>auth status</c> 没有跑登录；
    /// 模型目录用导入的令牌读取；页面、库文件里都没有令牌和刷新令牌。
    /// </summary>
    public static async Task ImportNamesByEmail()
    {
        using var claude = new FakeClaudeLauncher("success");
        await using var mock = await UpstreamFixture.Create();
        await using var web = await WebFixture.Create(overrides: claude.Register); await web.Login();
        var content = FakeClaude.Credential(ImportedToken, 60);
        using var page = await web.Post("/Upstream?handler=ImportAccount", await Form(web, mock, ""), "ImportFile", OfficialCredential.FileName, content);
        var raw = await page.Content.ReadAsStringAsync(); var html = WebUtility.HtmlDecode(raw);
        Check(page.IsSuccessStatusCode && html.Contains($"登录文件已导入，已保存为「{FakeClaude.Email}」。已获取 1 个模型") && html.Contains("<option value=\"fixture-model\""), html);
        Check(!raw.Contains(ImportedToken) && !raw.Contains("fixture-refresh-token"), "Imported tokens must not appear on the page");
        var candidate = web.Upstreams.LatestCandidate()!;
        Check(candidate.Name == FakeClaude.Email && candidate.Options is { OfficialLogin: true, Mode: "oauth", Authentication: "bearer" });
        var directory = Path.Combine(web.Home.Path, ".state", "official-accounts", candidate.Id);
        Check(File.ReadAllBytes(Path.Combine(directory, OfficialCredential.FileName)).SequenceEqual(content), "The imported file must be stored verbatim");
        var calls = File.ReadAllText(Path.Combine(directory, "fake-calls.log"));
        Check(calls.Contains("auth status --json") && !calls.Contains("auth login"), calls);
        Check(mock.Calls.Single(call => call.Path.Contains("/models")).Headers["Authorization"] == "Bearer " + ImportedToken);
        foreach (var file in Directory.GetFiles(Path.Combine(web.Home.Path, ".state"), "relay.db*"))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); using var copy = new MemoryStream(); stream.CopyTo(copy);
            Check(!Encoding.ASCII.GetString(copy.ToArray()).Contains(ImportedToken), "Imported tokens must stay out of the database: " + file);
        }
    }

    /// <summary>导入的访问令牌已过期、但有刷新令牌：允许导入，获取模型前等 CLI 刷新；测试、切换后转发用刷新后的新令牌。</summary>
    public static async Task ImportExpiredTokenRefreshes()
    {
        using var claude = new FakeClaudeLauncher("success");
        await using var mock = await UpstreamFixture.Create();
        await using var web = await WebFixture.Create(overrides: claude.Register); await web.Login();
        using (var page = await web.Post("/Upstream?handler=ImportAccount", await Form(web, mock, "过期的导入"), "ImportFile", OfficialCredential.FileName, FakeClaude.Credential(ImportedToken, -1)))
        {
            var html = WebUtility.HtmlDecode(await page.Content.ReadAsStringAsync());
            Check(page.IsSuccessStatusCode && html.Contains("登录文件已导入，已保存为「过期的导入」。已获取 1 个模型"), html);
        }
        Check(mock.Calls.Single(call => call.Path.Contains("/models")).Headers["Authorization"] == "Bearer " + FakeClaude.RefreshedToken, "Model discovery must wait for the refresh");
        var id = web.Upstreams.LatestCandidate()!.Id;
        web.Upstreams.ChooseModel(id, "fixture-model");
        var report = await web.Upstreams.TestCandidate(id, true, CancellationToken.None);
        Check(report.CanActivate, report.Message); web.Upstreams.Activate(id);
        var person = web.Identities.Create("导入测试");
        using (var relayed = await UpstreamFixture.RelayRequest(web, person.Token)) Check(relayed.StatusCode == HttpStatusCode.OK);
        Check(mock.Calls.Last().Headers["Authorization"] == "Bearer " + FakeClaude.RefreshedToken);
    }

    /// <summary>只有访问令牌没有刷新令牌：提示改用「贴账户访问令牌」；名称超长时在落盘前就拒绝。两者都不新增上游、不留账户目录。</summary>
    public static async Task ImportWithoutRefreshToken()
    {
        await Refused("success", FakeClaude.Credential(ImportedToken, 60, null), "没有刷新令牌（refreshToken），导入后到期不能自动续期。请改用下面的「贴账户访问令牌」。");
        using var claude = new FakeClaudeLauncher("success");
        await using var mock = await UpstreamFixture.Create();
        await using var web = await WebFixture.Create(overrides: claude.Register);
        var service = web.Upstreams;
        try { await service.ImportOfficial(service.ReadActive().Version, new UpstreamOptions { BaseUrl = mock.Url, AllowPrivateNetwork = true }, FakeClaude.Credential(ImportedToken, 60), new string('名', 41), CancellationToken.None); Check(false, "Overlong name must be refused"); }
        // 预期的拒绝：名称超长在落盘前就读出要求。
        catch (InvalidOperationException error) { Check(error.Message.Contains("供应商名称"), error.Message); }
        Check(NoAccountDirectories(web) && service.ListCandidates().Count == 0);
    }

    /// <summary>选的文件不是 JSON：读出「不是 JSON」与文件在哪，不复述文件内容。</summary>
    public static Task ImportNotJson() => Refused("success", Encoding.UTF8.GetBytes("not json " + ImportedToken), "选的文件不是 JSON，不是 Claude Code 的登录文件。");

    /// <summary>文件超过 64 KB：在解析之前就拒绝。</summary>
    public static Task ImportOversize() => Refused("success", JsonSerializer.SerializeToUtf8Bytes(new
    {
        claudeAiOauth = new { accessToken = ImportedToken, refreshToken = "fixture-refresh-token" }, padding = new string('x', OfficialCredential.MaxFileBytes)
    }), "选的文件超过 64 KB，不是 Claude Code 的登录文件。");

    /// <summary>本机没装 Claude Code：拒绝导入（续期要靠它），不建账户目录。</summary>
    public static Task ImportWithoutClaudeCode() => Refused("missing", FakeClaude.Credential(ImportedToken, 60), "本机没有找到 Claude Code，无法导入登录文件（令牌到期要靠它自动续期）。请先安装 Claude Code，再导入。");

    /// <summary>经网页导入 <paramref name="content"/>：页面读出含 <paramref name="expected"/> 的错误，不回显令牌，不新增上游，不留账户目录。</summary>
    private static async Task Refused(string scenario, byte[] content, string expected)
    {
        using var claude = new FakeClaudeLauncher(scenario);
        await using var mock = await UpstreamFixture.Create();
        await using var web = await WebFixture.Create(overrides: claude.Register); await web.Login();
        using var page = await web.Post("/Upstream?handler=ImportAccount", await Form(web, mock, "不合格的导入"), "ImportFile", OfficialCredential.FileName, content);
        var raw = await page.Content.ReadAsStringAsync(); var html = WebUtility.HtmlDecode(raw);
        Check(page.IsSuccessStatusCode && html.Contains(expected), expected + " / " + html);
        Check(!raw.Contains(ImportedToken), "Refusals must not echo the file");
        Check(web.Upstreams.ListCandidates().Count == 0 && NoAccountDirectories(web), expected);
    }

    /// <summary>「导入登录文件」表单的普通字段：名称、指向模拟上游的地址与私网开关、版本和防伪令牌。</summary>
    private static async Task<Dictionary<string, string>> Form(WebFixture web, UpstreamFixture mock, string name) => new()
    {
        ["Import.Name"] = name, ["Import.BaseUrl"] = mock.Url, ["Import.AllowPrivateNetwork"] = "true",
        ["Version"] = web.Upstreams.ReadActive().Version.ToString(), ["__RequestVerificationToken"] = await web.Csrf("/Upstream")
    };

    /// <summary>数据目录下没有任何官方账户目录（目录本身不存在或为空）。</summary>
    private static bool NoAccountDirectories(WebFixture web)
    {
        var root = Path.Combine(web.Home.Path, ".state", "official-accounts");
        return !Directory.Exists(root) || Directory.GetDirectories(root).Length == 0;
    }
}
