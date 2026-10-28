using System.Net;
using HouseholdRelay;
using static Tests;

static partial class FoundationTests
{
    public static async Task WebRateLimitAliases(bool setup, bool alternating)
    {
        await using var fixture = await WebFixture.Create();
        var code = fixture.Administrators.BootstrapCode()!;
        if (!setup) fixture.Administrators.Initialize(code, WebFixture.Password);
        var page = setup ? "/Setup" : "/Login";
        string[] aliases = [page, page + "/", page.ToLowerInvariant(), page.ToUpperInvariant() + "/",
            page.ToLowerInvariant() + "/?attempt=6", page + "?attempt=7"];
        // 先验证每种别名确实返回表单；GET 不应消耗 POST 的预算。
        foreach (var alias in aliases) _ = await fixture.Csrf(alias);
        var csrf = await fixture.Csrf(page);
        var fields = new Dictionary<string, string>
        {
            ["Password"] = setup ? WebFixture.Password : "incorrect-password",
            ["Confirmation"] = WebFixture.Password, ["Code"] = "incorrect-bootstrap-code",
            ["__RequestVerificationToken"] = csrf
        };
        var expected = setup ? (Administrators: 0, Sessions: 0, Bootstrap: 1) : (Administrators: 1, Sessions: 0, Bootstrap: 0);
        for (var index = 0; index < 5; index++)
        {
            var path = alternating ? aliases[index] : page;
            using var response = await fixture.Post(path, fields);
            Console.WriteLine($"POST {path} attempt {index + 1}: {(int)response.StatusCode}");
            Check(response.StatusCode == HttpStatusCode.OK
                && WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()).Contains(setup ? "引导码不正确" : "管理密码不正确"),
                $"Attempt {index + 1} must reach the credential check at {path}");
        }
        Check(AuthenticationCounts(fixture) == expected, "Incorrect credentials must not change authentication state");

        // 使用新 Cookie 会话与正确凭据，验证耗尽的预算不能被路径或浏览器会话绕过。
        using var retryClient = WebFixture.NewClient();
        fields["__RequestVerificationToken"] = await fixture.Csrf(page, retryClient);
        fields["Password"] = WebFixture.Password; fields["Code"] = code;
        foreach (var alias in aliases)
        {
            using var response = await fixture.Post(alias, fields, retryClient);
            var state = AuthenticationCounts(fixture);
            Console.WriteLine($"POST {alias} after limit: {(int)response.StatusCode}; administrators={state.Administrators}, sessions={state.Sessions}, bootstrap={state.Bootstrap}");
            Check(response.StatusCode == HttpStatusCode.TooManyRequests && state == expected,
                $"{alias} must return 429 without side effects; received {(int)response.StatusCode}, state {state}, expected {expected}");
            Check(response.Headers.RetryAfter?.Delta == TimeSpan.FromMinutes(1));
            Check(!response.Headers.TryGetValues("Set-Cookie", out var cookies) || !cookies.Any(value => value.StartsWith("hr_admin_", StringComparison.Ordinal)),
                "A rejected attempt must not issue an administrator cookie");
            // 即使预算耗尽仍能打开表单，且所有被测试的别名仍然有效。
            _ = await fixture.Csrf(alias, retryClient);
        }
        Check(fixture.Administrators.BootstrapCode() == (setup ? code : null), "Rate limiting must not consume the bootstrap credential");
        Check(fixture.Identities.List().Count == 0);
        using var protectedPage = await retryClient.GetAsync(fixture.Url + "/Users");
        Check(protectedPage.StatusCode == HttpStatusCode.Redirect, "Rejected credentials must not grant management access");
    }

    public static async Task WebRateLimitValidAttempts()
    {
        await using var fixture = await WebFixture.Create();
        var code = fixture.Administrators.BootstrapCode()!;
        foreach (var page in new[] { "/Setup", "/Login" })
        {
            var setup = page == "/Setup";
            var fields = new Dictionary<string, string>
            {
                ["Password"] = setup ? WebFixture.Password : "incorrect-password",
                ["Confirmation"] = WebFixture.Password, ["Code"] = "incorrect-bootstrap-code",
                ["__RequestVerificationToken"] = await fixture.Csrf(page)
            };
            foreach (var path in new[] { page, page + "/", page.ToLowerInvariant(), page.ToUpperInvariant() + "/" })
            {
                using var rejected = await fixture.Post(path, fields);
                Check(rejected.StatusCode == HttpStatusCode.OK);
            }
            fields["Password"] = WebFixture.Password; fields["Code"] = code;
            using var accepted = await fixture.Post(page.ToLowerInvariant() + "/", fields);
            Check(accepted.StatusCode == HttpStatusCode.Redirect, $"Fifth attempt with valid credentials must succeed at {page}");
            Check(AuthenticationCounts(fixture) == (1, setup ? 0 : 1, 0));
            Console.WriteLine($"POST {page.ToLowerInvariant()}/ fifth valid attempt: {(int)accepted.StatusCode}");
        }
        using var protectedPage = await fixture.Http.GetAsync(fixture.Url + "/Users");
        Check(protectedPage.StatusCode == HttpStatusCode.OK, "A permitted login must grant management access");
    }

    private static (int Administrators, int Sessions, int Bootstrap) AuthenticationCounts(WebFixture fixture)
    {
        using var connection = fixture.Database.Open();
        return (Convert.ToInt32(RelayDatabase.Scalar(connection, null, "SELECT COUNT(*) FROM administrators")),
            Convert.ToInt32(RelayDatabase.Scalar(connection, null, "SELECT COUNT(*) FROM admin_sessions")),
            Convert.ToInt32(RelayDatabase.Scalar(connection, null, "SELECT COUNT(*) FROM bootstrap")));
    }
}
