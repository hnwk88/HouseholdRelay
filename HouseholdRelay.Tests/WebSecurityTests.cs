using System.Net;
using HouseholdRelay;
using static Tests;

static partial class FoundationTests
{
    public static async Task WebSecurity()
    {
        await using var fixture = await WebFixture.Create();
        using (var anonymous = await fixture.Http.GetAsync(fixture.Url + "/Users")) Check(anonymous.StatusCode == HttpStatusCode.Redirect);
        using (var foreignHost = new HttpRequestMessage(HttpMethod.Get, fixture.Url + "/Setup"))
        {
            foreignHost.Headers.Host = "attacker.example";
            using var response = await fixture.Http.SendAsync(foreignHost); Check(response.StatusCode == HttpStatusCode.Forbidden);
        }
        var code = fixture.Administrators.BootstrapCode()!;
        using (var setup = await fixture.Http.GetAsync(fixture.Url + "/Setup"))
            Check(!(await setup.Content.ReadAsStringAsync()).Contains(code), "Setup must never serve the bootstrap code");
        var csrf = await fixture.Csrf("/Setup");
        var fields = new Dictionary<string, string>
        { ["Code"] = code, ["Password"] = WebFixture.Password, ["Confirmation"] = WebFixture.Password, ["__RequestVerificationToken"] = csrf };
        using (var forged = await fixture.Post("/Setup", fields, origin: "https://attacker.example")) Check(forged.StatusCode == HttpStatusCode.Forbidden);
        // 两个真正的 HTTP 表单同时初始化，只允许其中一个提交管理员身份。
        var races = await Task.WhenAll(fixture.Post("/Setup", fields), fixture.Post("/Setup", fields));
        Check(races.Count(response => response.StatusCode == HttpStatusCode.Redirect) == 1);
        foreach (var response in races) response.Dispose();
        Check(fixture.Administrators.IsInitialized && fixture.Administrators.BootstrapCode() is null);
        using (var locked = await fixture.Http.GetAsync(fixture.Url + "/Setup")) Check(locked.StatusCode == HttpStatusCode.NotFound);

        var issued = fixture.Identities.Create("个人令牌持有者");
        using var personalClient = WebFixture.NewClient(); personalClient.DefaultRequestHeaders.Authorization = new("Bearer", issued.Token);
        using (var forbidden = await personalClient.GetAsync(fixture.Url + "/Users")) Check(forbidden.StatusCode == HttpStatusCode.Redirect);
        using (var write = await fixture.Post("/Users?handler=Create", new() { ["displayName"] = "不应创建" }, personalClient))
            Check(write.StatusCode == HttpStatusCode.Redirect);
        var loginFields = new Dictionary<string, string> { ["Password"] = WebFixture.Password, ["__RequestVerificationToken"] = await fixture.Csrf("/Login") };
        using var login = await fixture.Post("/Login", loginFields); Check(login.StatusCode == HttpStatusCode.Redirect);
        var cookieHeader = login.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("hr_admin_", StringComparison.Ordinal));
        Check(cookieHeader.Contains("httponly", StringComparison.OrdinalIgnoreCase) && cookieHeader.Contains("samesite=strict", StringComparison.OrdinalIgnoreCase));
        var cookie = cookieHeader.Split(';')[0];
        using (var list = await fixture.Http.GetAsync(fixture.Url + "/Users"))
        {
            var html = await list.Content.ReadAsStringAsync();
            Check(list.IsSuccessStatusCode && !html.Contains(issued.Token) && !html.Contains(WebFixture.Password) && !html.Contains(code));
            Check(list.Headers.CacheControl?.NoStore == true);
        }
        using (var cookieOnly = await fixture.Http.GetAsync(fixture.Url + "/status"))
            Check(cookieOnly.StatusCode == HttpStatusCode.Unauthorized, "Administrator cookie must not act as a relay token");
        using (var noCsrf = await fixture.Post("/Users?handler=Create", new() { ["displayName"] = "不应创建" }))
            Check(noCsrf.StatusCode == HttpStatusCode.BadRequest);
        using (var foreignOrigin = await fixture.Post("/Users?handler=Create", new() { ["displayName"] = "不应创建", ["__RequestVerificationToken"] = await fixture.Csrf("/Users") }, origin: "https://attacker.example"))
            Check(foreignOrigin.StatusCode == HttpStatusCode.Forbidden);
        using (var noOrigin = await fixture.Http.PostAsync(fixture.Url + "/Users?handler=Create", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["displayName"] = "不应创建", ["__RequestVerificationToken"] = await fixture.Csrf("/Users") })))
            Check(noOrigin.StatusCode == HttpStatusCode.Forbidden);
        Check(fixture.Identities.List().Count == 1);
        using (var create = await fixture.Post("/Users?handler=Create", new() { ["displayName"] = "网页用户", ["__RequestVerificationToken"] = await fixture.Csrf("/Users") }))
            Check(create.IsSuccessStatusCode && (await create.Content.ReadAsStringAsync()).Contains("id=\"issued-token\""));
        Check(fixture.Identities.List().Count == 2);
        using (var notReady = new HttpRequestMessage(HttpMethod.Post, fixture.Url + "/v1/messages"))
        {
            notReady.Headers.Authorization = new("Bearer", issued.Token); notReady.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
            using var response = await personalClient.SendAsync(notReady); Check(response.StatusCode == HttpStatusCode.ServiceUnavailable);
        }
        using (var logout = await fixture.Post("/Logout", new() { ["__RequestVerificationToken"] = await fixture.Csrf("/Users") }))
            Check(logout.StatusCode == HttpStatusCode.Redirect);
        using var replay = WebFixture.NewClient(); replay.DefaultRequestHeaders.Add("Cookie", cookie);
        using (var loggedOut = await replay.GetAsync(fixture.Url + "/Users")) Check(loggedOut.StatusCode == HttpStatusCode.Redirect, "Copied logout cookie must be revoked");

        using var bruteForce = WebFixture.NewClient(); var loginCsrf = await fixture.Csrf("/Login", bruteForce); var limited = false;
        for (var index = 0; index < 6; index++)
        {
            using var response = await fixture.Post("/Login", new() { ["Password"] = "incorrect", ["__RequestVerificationToken"] = loginCsrf }, bruteForce);
            limited |= response.StatusCode == HttpStatusCode.TooManyRequests;
        }
        Check(limited, "Login rate limit must apply to actual form requests");
        // 独占占用真实数据库，让首次设置路由读取失败；不能显示一个新的设置表单。
        using var databaseLock = new FileStream(fixture.Database.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var unavailable = await fixture.Http.GetAsync(fixture.Url + "/Setup");
        Check(unavailable.StatusCode == HttpStatusCode.ServiceUnavailable && !(await unavailable.Content.ReadAsStringAsync()).Contains("<form"));
    }
}
