using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Core.Usage;
using static Tests.TestSuite;
using static Tests.UsageFixture;

namespace Tests;

/// <summary>网页读屏（评估整改第四批）：上游一家一个菜单按钮、小表单与焦点落点，用量每个数带单位、币种读中文名，按上游筛用量，字段错误挂到输入框，标题带结果。</summary>
static class PageReadingTests
{
    /// <summary>
    /// 上游页：每家只有一个可 Tab 到的按钮（菜单项 tabindex=-1，菜单默认收起），按钮名是「上游名，状态」；原来平铺的改名框、删除勾选框都不在列表里。
    /// 菜单「改名」进来展开只管这一家的小表单并点名焦点进输入框；改名为空时错误挂到输入框（aria-invalid、aria-describedby 指向错误文字），成功后焦点回这一家的按钮；
    /// 菜单「删除」进来展开确认小表单，没勾确认时错误挂到勾选框；删掉后焦点去列表里的下一家。自动切换是一个 role=switch 的开关。
    /// </summary>
    public static async Task UpstreamMenuPage()
    {
        await using var mock = await UpstreamFixture.Create(); await using var web = await WebFixture.Create(); await web.Login();
        var service = web.Upstreams;
        var active = await mock.Activate(service);
        var spare = await service.SaveCandidate((await service.ReadActive()).ActiveVersion, mock.Options(), "fixture-spare-secret", "备用甲");
        var last = await service.SaveCandidate((await service.ReadActive()).ActiveVersion, mock.Options(), "fixture-last-secret", "备用乙");
        var html = await Page(web, "/Upstream");
        var list = Section(html, "saved");
        Check(Regex.Matches(list, "<button[^>]*class=\"upstream-button\"").Count == 3, "Each upstream has exactly one menu button");
        Check(!Regex.IsMatch(list, "<(input|select|textarea)(?![^>]*type=\"hidden\")[^>]*>") || list.Contains("id=\"test-model\""), "No visible per-upstream inputs outside the opened small form: " + list);
        foreach (Match item in Regex.Matches(list, "<[^>]*role=\"menuitem\"[^>]*>")) Check(item.Value.Contains("tabindex=\"-1\""), "Menu items are not Tab stops: " + item.Value);
        Check(Regex.Matches(list, "<ul role=\"menu\"[^>]*hidden").Count == 3, "Menus start collapsed");
        Check(list.Contains($"id=\"upstream-{spare}\" class=\"upstream-button\" aria-haspopup=\"menu\" aria-expanded=\"false\" aria-controls=\"upstream-menu-{spare}\">备用甲，尚未测试</button>"), list);
        Check(Regex.IsMatch(list, $"id=\"upstream-{active}\"[^>]*>[^<]*，在用，已通过</button>"), "The active upstream reads 在用，已通过");
        Check(UpstreamDetectionTests.MenuActions(html, active).First() == "usage" && UpstreamDetectionTests.MenuActions(html, spare).Contains("delete"), "Menu order comes from the domain");
        Check(Regex.IsMatch(html, "<button type=\"submit\" id=\"failover-switch\" role=\"switch\" aria-checked=\"(true|false)\""), "Auto switch is one switch");

        var rename = await Page(web, $"/Upstream?rename={spare}");
        Check(rename.Contains("data-focus-id=\"rename-name\"") && rename.Contains("「备用甲」的新名称") && Regex.Matches(rename, "<input id=\"rename-name\"").Count == 1, "The rename small form opens under that upstream with focus on its input");
        var empty = await Post(web, "/Upstream?handler=Rename", new() { ["candidateId"] = spare.ToString(CultureInfo.InvariantCulture), ["newName"] = " " });
        Check(Regex.IsMatch(empty, "id=\"rename-name\"[^>]*aria-invalid=\"true\" aria-describedby=\"rename-name-error\"") && empty.Contains("<p id=\"rename-name-error\" class=\"field-error\">"), "Rename error is attached to the input: " + Section(empty, "saved"));
        Check(Regex.IsMatch(empty, "<title>没有完成：[^<]+ · 上游接入 · Household Relay</title>"), "Title carries the error");
        var renamed = await Post(web, "/Upstream?handler=Rename", new() { ["candidateId"] = spare.ToString(CultureInfo.InvariantCulture), ["newName"] = "备用丙" });
        Check(renamed.Contains($"data-focus-id=\"upstream-{spare}\"") && renamed.Contains("<title>已改名为「备用丙」。 · 上游接入 · Household Relay</title>") && renamed.Contains("id=\"notice\""), "After rename focus goes back to that upstream's button and the title reads the result");

        var unconfirmed = await Post(web, "/Upstream?handler=Delete", new() { ["candidateId"] = spare.ToString(CultureInfo.InvariantCulture), ["deleteConfirmed"] = "false" });
        Check(Regex.IsMatch(unconfirmed, "id=\"delete-confirm\"[^>]*aria-invalid=\"true\" aria-describedby=\"delete-confirm-error\""), "Unconfirmed delete error is attached to the checkbox");
        var deleted = await Post(web, "/Upstream?handler=Delete", new() { ["candidateId"] = spare.ToString(CultureInfo.InvariantCulture), ["deleteConfirmed"] = "true" });
        Check(deleted.Contains($"data-focus-id=\"upstream-{last}\""), "After delete focus goes to the next upstream");
        var gone = await Post(web, "/Upstream?handler=Delete", new() { ["candidateId"] = last.ToString(CultureInfo.InvariantCulture), ["deleteConfirmed"] = "true" });
        Check(gone.Contains($"data-focus-id=\"upstream-{active}\""), "Deleting the last one moves focus to the one before it");
    }

    /// <summary>
    /// 单位：token 计数读「12,345 个 token」、未知读「未知」，次数读「30 次」；币种读中文名（USD 美元、CNY 人民币），认不出的照原代码读；
    /// 页面脚本拿到的币种表与 C# 用的是同一份；个人页不要令牌就能取标签表。管理端用量页的汇总读句每个数都带单位，表头带「（token）」「（次）」，金额读「0.03 美元」。
    /// </summary>
    public static async Task UsageUnits()
    {
        Check(UsageLabels.Tokens(null) == "未知" && UsageLabels.Tokens(12345) == 12345L.ToString("N0") + " 个 token" && UsageLabels.Times(30) == "30 次", "Token and count units");
        Check(UsageLabels.Currency("USD") == "美元" && UsageLabels.Currency("CNY") == "人民币" && UsageLabels.Currency("XYZ") == "XYZ", "Currency names");
        Check(UsageLabels.Amount(0.53m, "USD") == "0.53 美元" && UsageLabelTable.Instance.Currencies["CNY"] == "人民币", "Amount reads the currency name; the script table is the same map");
        await using var upstream = await UpstreamFixture.Create(); await using var web = await WebFixture.Create(); await web.Login();
        var user = await web.Identities.Create("单位"); await upstream.Activate(web.Upstreams);
        upstream.Handler = context => Json(context, new { input_tokens = 12345, output_tokens = 20, cost_usd = 0.03m });
        Check(await Call(web, user.Token, "reported") == HttpStatusCode.OK); await Settled(web, 1);
        var page = await Page(web, "/Usage");
        var inference = Regex.Match(page, "<section class=\"card\" data-usage-kind=\"inference\">(.*?)</section>", RegexOptions.Singleline).Groups[1].Value;
        Check(inference.Contains("推理：请求 1 次") && inference.Contains("成功 1 次，失败 0 次") && inference.Contains($"已知输入 <strong>{12345L:N0} 个 token</strong>") && inference.Contains("已知输出 <strong>20 个 token</strong>"), inference);
        Check(inference.Contains("已知金额（上游报告）<strong>0.03 美元</strong>，请求 1 次") && !inference.Contains("USD"), "Money reads the currency name: " + inference);
        // 汇总读句里每个数字后面都紧跟单位（次、个 token、美元）或本身是「未知」：把任一处单位撤掉，这一条就红。
        var bare = Regex.Matches(Regex.Replace(inference, "<[^>]+>", ""), @"\d[\d,.]*(?![\d,.])(?! ?(次|个 token|美元))").Select(item => item.Value).ToArray();
        Check(bare.Length == 0, "Numbers without a unit: " + string.Join(" | ", bare));
        Check(page.Contains("<th>已知输入 / 输出（token）</th>") && page.Contains("<th>请求 / 成功（次）</th>") && page.Contains("aria-label=\"用量明细\"") && !page.Contains("可横向滚动"), "Table headers carry units");
        Check(Regex.IsMatch(page, "<nav class=\"actions\" aria-label=\"明细分页\">"), "Pagination is a named nav");
        using var labels = await web.Http.GetAsync(web.Url + "/my-usage/labels");
        using var json = JsonDocument.Parse(await labels.Content.ReadAsStringAsync());
        Check(labels.IsSuccessStatusCode && json.RootElement.GetProperty("currencies").GetProperty("USD").GetString() == "美元" && json.RootElement.GetProperty("kinds").GetProperty("inference").GetString() == "推理", "Personal page reads the label table without a token");
    }

    /// <summary>按上游筛用量：只给记账为那一家的请求；页面读出「只看「某某」这一家」，翻页链接带着条件；上游主键为 0 时按条件无效拒绝。</summary>
    public static async Task UsageByUpstream()
    {
        await using var upstream = await UpstreamFixture.Create(); await using var web = await WebFixture.Create(); await web.Login();
        var user = await web.Identities.Create("筛选"); var first = await upstream.Activate(web.Upstreams);
        Check(await Call(web, user.Token) == HttpStatusCode.OK);
        var second = await upstream.Activate(web.Upstreams, "fixture-second-upstream-secret");
        Check(await Call(web, user.Token) == HttpStatusCode.OK && await Call(web, user.Token) == HttpStatusCode.OK); await Settled(web, 3);
        Check((await web.RelayUsage.Query(new(UpstreamCandidateId: first))).Total == 1 && (await web.RelayUsage.Query(new(UpstreamCandidateId: second))).Total == 2, "Filter keeps only that upstream's requests");
        try { new UsageFilter(UpstreamCandidateId: 0).Validate(); Check(false, "Upstream id 0 must be refused"); }
        // 预期的拒绝：上游主键必须是正数。
        catch (InvalidOperationException error) { Check(error.Message == "查询条件无效，请检查时间、接口、结果和页码。", error.Message); }
        var name = (await web.Upstreams.ReadCandidate(second)).Name;
        var page = await Page(web, "/Usage?UpstreamCandidateId=" + second);
        Check(page.Contains($"只看「{name}」这一家上游记账的请求。") && page.Contains("共请求 2 次。") && page.Contains($"<option value=\"{second}\" selected=\"selected\">"), "Usage page reads the upstream filter");
    }

    /// <summary>
    /// 使用者页：按钮和输入框带人名，改名不合规则时错误挂到那一人的显示名输入框；改名、停用后焦点回到那一人的那个按钮；没有使用者时读出下一步。
    /// 登录页：密码不对时错误挂到密码框，标题读「没有完成：管理密码不正确。」。
    /// </summary>
    public static async Task FieldErrorsAndTitles()
    {
        await using var web = await WebFixture.Create(); await web.Login();
        var none = await Page(web, "/Users");
        Check(none.Contains("还没有使用者。请在上面「添加使用者」里填显示名"), "Empty user list reads the next step");
        var user = await web.Identities.Create("小王"); var id = user.UserId.ToString(CultureInfo.InvariantCulture);
        var listing = await Page(web, "/Users");
        Check(listing.Contains("「小王」的显示名") && listing.Contains("保存「小王」的显示名") && listing.Contains("停用「小王」（他的个人令牌立即不能用）") && listing.Contains("轮换「小王」的令牌（旧令牌立即失效）")
            && listing.Contains($"aria-labelledby=\"user-{id}-heading\""), "Buttons and sections carry the person's name");
        var tooLong = await Post(web, "/Users?handler=Rename", new() { ["userId"] = id, ["displayName"] = new string('名', 65) });
        Check(Regex.IsMatch(tooLong, $"id=\"name-{id}\"[^>]*aria-invalid=\"true\" aria-describedby=\"name-{id}-error\""), "Rename error is attached to that person's input");
        var disabled = await Post(web, "/Users?handler=Enable", new() { ["userId"] = id, ["enabled"] = "false" });
        Check(disabled.Contains($"data-focus-id=\"enable-{id}\"") && disabled.Contains("启用「小王」") && disabled.Contains("<title>已保存，新的权限检查立即生效。已发往上游的请求可以结束。 · 使用者与令牌 · Household Relay</title>"), "Focus returns to that person's button; title reads the result");
        using var anonymous = new HttpClient();
        var token = await web.Csrf("/Login", anonymous);
        using var login = new HttpRequestMessage(HttpMethod.Post, web.Url + "/Login") { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["Password"] = "wrong-password-for-fixture", ["__RequestVerificationToken"] = token }) };
        login.Headers.TryAddWithoutValidation("Origin", web.Url);
        using var refused = await anonymous.SendAsync(login);
        var html = WebUtility.HtmlDecode(await refused.Content.ReadAsStringAsync());
        Check(Regex.IsMatch(html, "id=\"Password\"[^>]*aria-invalid=\"true\"|aria-invalid=\"true\"[^>]*id=\"Password\"") && html.Contains("<title>没有完成：管理密码不正确。 · "), "Login error is attached to the password input: " + html);
    }

    /// <summary>取一页（已登录），返回解码后的正文。</summary>
    private static async Task<string> Page(WebFixture web, string path)
    {
        using var response = await web.Http.GetAsync(web.Url + path);
        Check(response.IsSuccessStatusCode, path + " " + (int)response.StatusCode);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    /// <summary>带防 CSRF 令牌提交一张表单（已登录），返回解码后的正文。</summary>
    private static async Task<string> Post(WebFixture web, string path, Dictionary<string, string> values)
    {
        values["__RequestVerificationToken"] = await web.Csrf(path.Split('?')[0]);
        using var response = await web.Post(path, values);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    /// <summary>页面里某个 id 的 section 正文（到下一个顶层 section 开始为止）。</summary>
    private static string Section(string html, string id)
    {
        var start = html.IndexOf($"id=\"{id}\"", StringComparison.Ordinal);
        if (start < 0) return "";
        var end = html.IndexOf("<section class=\"card\"", start, StringComparison.Ordinal);
        return end < 0 ? html[start..] : html[start..end];
    }
}
