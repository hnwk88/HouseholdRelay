using System.Text.Json;
using static Tests.TestSuite;
using Infrastructure.ExternalSources.ClaudeCode;

namespace Tests;

/// <summary>用假 CLI 和回环上游逐步核对账户生命周期的 canonical 编号、代次与私有 userID，不读取正式数据。</summary>
public static class DeviceIdentityAccountLifecycleTests
{
    /// <summary>重新登录、加删账户、Claude 升级和跨站账户迁移均保持各站自身编号；新目的站不继承源站设备值。</summary>
    public static async Task AccountsAndImportsPreserveCanonical()
    {
        using var claude = new FakeClaudeLauncher("success");
        await using var mock = await UpstreamFixture.Create();
        await using var source = await WebFixture.Create(overrides: claude.Register);
        var original = await Current(source);
        var first = await source.Upstreams.LoginOfficialAsync((await source.Upstreams.ReadActiveAsync()).ActiveVersion, mock.Options(), "首个账户", default); var firstUid = await Uid(source, first);
        await AssertUnchanged(source, original, firstUid);
        var accountDirectory = AccountDirectory(source, firstUid);
        var calls = Path.Combine(accountDirectory, "fake-calls.log");
        var loginCount = File.ReadAllLines(calls).Count(line => line == "auth login");
        await source.App.Services.GetRequiredService<IOfficialAccountLogin>().LoginAsync(firstUid, default);
        await AssertUnchanged(source, original, firstUid);
        Check(File.ReadAllLines(calls).Count(line => line == "auth login") == loginCount + 1, "Relogin must actually execute auth login in the same isolated account directory.");
        Evidence(6, "relogin-same-private-account");

        var added = await source.Upstreams.LoginOfficialAsync((await source.Upstreams.ReadActiveAsync()).ActiveVersion, mock.Options(), "新增账户", default); var addedUid = await Uid(source, added);
        Check((await source.Upstreams.ListCandidatesAsync()).Count == 2 && added != first);
        await AssertUnchanged(source, original, firstUid, addedUid); Evidence(7, "add-official-account");
        await source.Upstreams.DeleteAsync(added);
        Check((await source.Upstreams.ListCandidatesAsync()).Count == 1 && !Directory.Exists(AccountDirectory(source, addedUid)));
        await AssertUnchanged(source, original, firstUid); Evidence(8, "delete-official-account");

        Check((await source.Upstreams.TestCandidateAsync(first, true, default)).CanActivate); await source.Upstreams.ActivateAsync(first);
        var person = await source.Identities.CreateAsync("升级对照");
        var message = DeviceMetadataPathTests.Sample(false, new string('a', 64), "fixture-model", claude.Version, "sdk-cli");
        using (var request = UsageFixture.Request(source, person.Token, "before-upgrade", body: message))
        using (var response = await source.Http.SendAsync(request)) Check(response.IsSuccessStatusCode);
        Check(mock.Calls.Last().Headers["User-Agent"] == "claude-cli/1.0.0 (external, sdk-cli)");
        claude.Reinstall("success", "1.0.1");
        message = DeviceMetadataPathTests.Sample(false, new string('b', 64), "fixture-model", claude.Version, "sdk-cli");
        using (var request = UsageFixture.Request(source, person.Token, "after-upgrade", body: message))
        using (var response = await source.Http.SendAsync(request)) Check(response.IsSuccessStatusCode);
        Check(mock.Calls.Last().Headers["User-Agent"] == "claude-cli/1.0.1 (external, sdk-cli)" && mock.Calls.Last().Body.Contains(original.DeviceId, StringComparison.Ordinal),
            "The upgraded fake CLI must be recaptured while the outgoing site device stays unchanged.");
        await AssertUnchanged(source, original, firstUid); Evidence(5, "claude-upgrade-and-identity-recapture");

        var credentialPath = Path.Combine(accountDirectory, OfficialCredential.FileName);
        var originalCredential = File.ReadAllBytes(credentialPath);
        const string password = "fixture-account-identity-export-password";
        var archive = (await source.Upstreams.ExportAsync([first], password)).File;
        await AssertUnchanged(source, original, firstUid);
        Check((await source.Upstreams.ReadCandidateAsync(first)).ExportedAt is not null && !(await source.Upstreams.ReadActiveAsync()).Configured);
        var callsAfterExport = File.ReadAllLines(calls).Length;
        Check(source.App.Services.GetRequiredService<IOfficialAccountLogin>().ReadAccessToken(firstUid) is null && File.ReadAllLines(calls).Length == callsAfterExport,
            "The exported source account does not read or refresh its credential again.");
        Check(File.ReadAllBytes(credentialPath).SequenceEqual(originalCredential), "Export and identity checks never rewrite the source credential.");
        Evidence(9, "export-stops-source-account");

        await using var existing = await WebFixture.Create(overrides: claude.Register);
        var existingIdentity = await Current(existing);
        await existing.Upstreams.SaveCandidateAsync((await existing.Upstreams.ReadActiveAsync()).ActiveVersion, mock.Options(), "fixture-existing-api-key", "原有 API");
        Check((await existing.Upstreams.ListCandidatesAsync()).Count == 1 && existingIdentity.DeviceId != original.DeviceId);
        var imported = await existing.Upstreams.ImportAsync((await existing.Upstreams.ReadActiveAsync()).ActiveVersion, archive, password, default);
        Check(imported.Count == 1 && imported[0].UpstreamCandidateId is not null && imported[0].Error is null);
        await AssertUnchanged(existing, existingIdentity, await Uid(existing, imported[0].UpstreamCandidateId!.Value));
        Check(File.ReadAllBytes(Path.Combine(AccountDirectory(existing, await Uid(existing, imported[0].UpstreamCandidateId!.Value)), OfficialCredential.FileName)).SequenceEqual(originalCredential));
        Evidence(10, "import-into-existing-site");

        await using var fresh = await WebFixture.Create(overrides: claude.Register);
        var freshIdentity = await Current(fresh);
        Check((await fresh.Upstreams.ListCandidatesAsync()).Count == 0 && freshIdentity.Version == 1 && freshIdentity.DeviceId != original.DeviceId && freshIdentity.DeviceId != existingIdentity.DeviceId);
        var freshImport = await fresh.Upstreams.ImportAsync((await fresh.Upstreams.ReadActiveAsync()).ActiveVersion, archive, password, default);
        Check(freshImport.Count == 1 && freshImport[0].UpstreamCandidateId is not null && freshImport[0].Error is null);
        await AssertUnchanged(fresh, freshIdentity, await Uid(fresh, freshImport[0].UpstreamCandidateId!.Value));
        Check(File.ReadAllBytes(Path.Combine(AccountDirectory(fresh, await Uid(fresh, freshImport[0].UpstreamCandidateId!.Value)), OfficialCredential.FileName)).SequenceEqual(originalCredential));
        await AssertUnchanged(source, original, firstUid);
        Evidence(11, "import-into-new-site-uses-destination-identity");
    }

    /// <summary>直接读取同一实例的持久身份，不借上游状态读取触发任何账户操作。</summary>
    private static Task<SiteIdentitySnapshot> Current(WebFixture web) => web.App.Services.SiteIdentityStore().ReadAsync();

    /// <summary>逐项比较数据库、普通设置、运行快照以及现有账户派生值，不能只检查请求成功。</summary>
    private static async Task AssertUnchanged(WebFixture web, SiteIdentitySnapshot expected, params string[] accounts)
    {
        Check(await Current(web) == expected && (await web.Settings.ReadAsync()).Value.DeviceId == expected.DeviceId && web.App.Services.GetRequiredService<ISiteIdentityService>().Current == expected,
            "Canonical database identity, settings and runtime generation must all remain unchanged.");
        foreach (var account in accounts)
        {
            using var configuration = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AccountDirectory(web, account), ".claude.json")));
            Check(configuration.RootElement.GetProperty("userID").GetString() == expected.DeviceId, "An account uses its destination site's canonical identity.");
        }
    }

    /// <summary>已保存上游的对外编号，官方账户目录按它命名。</summary>
    private static async Task<string> Uid(WebFixture web, long id) => (await web.Upstreams.ReadCandidateAsync(id)).Uid;

    /// <summary>测试账户目录只位于当前夹具的私有状态根下。</summary>
    private static string AccountDirectory(WebFixture web, string account) => Path.Combine(web.Home.Path, ".state", "official-accounts", account);

    /// <summary>输出验收十六项中的对应实测行，不输出原始编号或任何凭据。</summary>
    private static void Evidence(int scenario, string action) => Console.WriteLine($"IDENTITY LIFECYCLE {scenario:D2}: {action}; canonical_unchanged=true; generation_unchanged=true; derived_user_id_matches=true; evidence=fake-cli-and-loopback.");
}
