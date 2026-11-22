using System.Diagnostics;
using System.Text;
using Application.Interfaces.ClaudeCode;
using Application.Interfaces.Upstream;
using Core.Siyu;
using Infrastructure.ExternalSources.ClaudeCode;
using Microsoft.Extensions.DependencyInjection;
using static Tests.TestSuite;

namespace Tests;

/// <summary>思域取官方凭据与启动资料：只给确实有效的令牌，过期续期失败绝不退回旧令牌；凭据代次随令牌内容变；资料按字段表核对。全部用假 Claude，不碰真实 CLI。</summary>
public static class SiyuCredentialTests
{
    private const string AccountId = "fedcba0987654321fedcba0987654321";

    /// <summary>有效直接用、同令牌同代次；过期后续期成功换新令牌并加一代；没写到期、续期失败、账户已删一律拒发。</summary>
    public static async Task AcquireCredentialNeverFallsBack()
    {
        using var claude = new FakeClaudeLauncher("success");
        await using var web = await WebFixture.Create(overrides: claude.Register);
        var (accounts, directory) = Prepare(web);

        FakeClaude.Write(directory, "fixture-siyu-valid", 60);
        var first = await accounts.AcquireCredential(AccountId, default);
        var again = await accounts.AcquireCredential(AccountId, default);
        Check(first.AccessToken == "fixture-siyu-valid" && first.Generation == 1 && again.Generation == 1, "有效令牌直接用，同一令牌同一代次");
        Check(!first.ToString().Contains("fixture-siyu-valid", StringComparison.Ordinal), "ToString 不带令牌");
        Check(!File.Exists(Path.Combine(directory, "fake-calls.log")) || !File.ReadAllText(Path.Combine(directory, "fake-calls.log")).Contains("/usage", StringComparison.Ordinal), "离到期远不起续期");

        FakeClaude.Write(directory, "fixture-siyu-expired", -1);
        var renewed = await accounts.AcquireCredential(AccountId, default);
        Check(renewed.AccessToken == FakeClaude.RefreshedToken && renewed.Generation == 2 && renewed.ExpiresAt > DateTimeOffset.UtcNow, "过期后等续期，拿到新令牌、代次加一");

        File.WriteAllText(Path.Combine(directory, OfficialCredential.FileName), """{"claudeAiOauth":{"accessToken":"fixture-no-expiry","refreshToken":"r"}}""");
        await Refused(() => accounts.AcquireCredential(AccountId, default), "official-expiry-unknown", "没写到期时刻");

        // 只删本用例临时数据目录里的假凭据，模拟凭据被移走。
        File.Delete(Path.Combine(directory, OfficialCredential.FileName));
        await Refused(() => accounts.AcquireCredential(AccountId, default), "official-reauthentication-required", "凭据不在");

        accounts.Remove(AccountId);
        await Refused(() => accounts.AcquireCredential(AccountId, default), "official-account-gone", "账户已删除");
    }

    /// <summary>过期且续期失败：重读仍是旧的过期令牌，必须拒发，不能把旧令牌交出去。</summary>
    public static async Task ExpiredAndRefreshFailedIsRefused()
    {
        using var claude = new FakeClaudeLauncher("refresh-fails");
        await using var web = await WebFixture.Create(overrides: claude.Register);
        var (accounts, directory) = Prepare(web);
        FakeClaude.Write(directory, "fixture-siyu-stale", -1);
        await Refused(() => accounts.AcquireCredential(AccountId, default), "official-reauthentication-required", "过期且续期失败");
        Check(File.ReadAllText(Path.Combine(directory, "fake-calls.log")).Contains("-p /usage", StringComparison.Ordinal), "确实起过一次续期");
        Check(accounts.ReadAccessToken(AccountId) == "fixture-siyu-stale", "旧接法的读法不变（本单不动旧路径）");
    }

    /// <summary>调用方取消只放弃自己的等待：很快返回取消，不等那次续期结束。</summary>
    public static async Task CancellationOnlyStopsTheWaiter()
    {
        using var claude = new FakeClaudeLauncher("slow-refresh");
        await using var web = await WebFixture.Create(overrides: claude.Register);
        var (accounts, directory) = Prepare(web);
        FakeClaude.Write(directory, "fixture-siyu-slow", -1);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var watch = Stopwatch.StartNew(); var cancelled = false;
        try { await accounts.AcquireCredential(AccountId, cancel.Token); }
        // 调用方自己的取消是预期结果。
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && watch.Elapsed < TimeSpan.FromSeconds(10), $"取消应很快返回，实际 {watch.Elapsed}");
        Check(File.ReadAllText(Path.Combine(directory, "fake-calls.log")).Contains("-p /usage", StringComparison.Ordinal), "续期已经起了");
        accounts.Remove(AccountId);
    }

    /// <summary>启动资料：合表就返回原字节与三项凭据资料；缺 oauthAccount、凭据不在、设备编号没对齐都本地拒绝。</summary>
    public static async Task LaunchProfileFromAccountDirectory()
    {
        using var claude = new FakeClaudeLauncher("success");
        await using var web = await WebFixture.Create(overrides: claude.Register);
        var (accounts, directory) = Prepare(web);
        var deviceId = web.App.Services.GetRequiredService<IClaudeConfigurationPreparation>().DeviceId;
        const string Account = """{"accountUuid":"0f6c2a1e-3b7d-4c55-9a10-2f4e8d9b1c01","emailAddress":"someone@example.com","profileFetchedAt":1795327787407}""";
        var configuration = Path.Combine(directory, ".claude.json");
        File.WriteAllText(configuration, "{\"userID\":\"" + deviceId + "\",\"oauthAccount\":" + Account + ",\"machineID\":\"keep\"}");
        File.WriteAllText(Path.Combine(directory, OfficialCredential.FileName),
            """{"claudeAiOauth":{"accessToken":"fixture-secret","refreshToken":"fixture-refresh","expiresAt":1,"scopes":["user:inference","user:mcp_servers"],"subscriptionType":"max","rateLimitTier":"default_claude_max_5x"}}""");

        var profile = accounts.ReadLaunchProfile(AccountId);
        Check(profile.AccountUuid == "0f6c2a1e-3b7d-4c55-9a10-2f4e8d9b1c01" && profile.OAuthAccountJson.Span.SequenceEqual(Encoding.UTF8.GetBytes(Account)), "oauthAccount 原字节");
        var metadata = Encoding.UTF8.GetString(profile.CredentialMetadataJson.Span);
        Check(metadata == """{"scopes":["user:inference","user:mcp_servers"],"subscriptionType":"max","rateLimitTier":"default_claude_max_5x"}""" && !metadata.Contains("fixture-secret", StringComparison.Ordinal), "凭据资料只有三项");
        Check(!File.Exists(Path.Combine(directory, "fake-calls.log")), "读资料不起 CLI");

        File.WriteAllText(configuration, "{\"userID\":\"" + new string('b', 64) + "\",\"oauthAccount\":" + Account + "}");
        RefusedNow(() => accounts.ReadLaunchProfile(AccountId), "account-configuration", "设备编号没对齐");

        File.WriteAllText(configuration, "{\"userID\":\"" + deviceId + "\"}");
        RefusedNow(() => accounts.ReadLaunchProfile(AccountId), "profile-missing", "没有 oauthAccount");

        File.WriteAllText(configuration, "{\"userID\":\"" + deviceId + "\",\"oauthAccount\":" + Account + "}");
        // 只删本用例临时数据目录里的假凭据，模拟凭据被移走。
        File.Delete(Path.Combine(directory, OfficialCredential.FileName));
        RefusedNow(() => accounts.ReadLaunchProfile(AccountId), "official-reauthentication-required", "凭据不在");
    }

    /// <summary>建好受保护的账户目录并对齐设备编号。</summary>
    private static (IOfficialAccountLogin Accounts, string Directory) Prepare(WebFixture web)
    {
        var directory = Path.Combine(web.Home.Path, ".state", "official-accounts", AccountId);
        WindowsSecrets.ProtectDirectory(directory);
        File.WriteAllText(Path.Combine(directory, ".claude.json"), "{}");
        web.App.Services.GetRequiredService<IClaudeConfigurationPreparation>().PrepareAccount(AccountId);
        return (web.App.Services.GetRequiredService<IOfficialAccountLogin>(), directory);
    }

    /// <summary>异步操作应以指定原因码的本地拒绝结束。</summary>
    private static async Task Refused<T>(Func<Task<T>> action, string code, string label)
    {
        try { await action(); Check(false, label + "：应拒绝"); }
        // 预期的本地拒绝：核对原因码。
        catch (SiyuRefusal refusal) { Check(refusal.Code == code, $"{label}：应是 {code}，实际 {refusal.Code}"); }
    }

    /// <summary>同步操作应以指定原因码的本地拒绝结束。</summary>
    private static void RefusedNow<T>(Func<T> action, string code, string label)
    {
        try { action(); Check(false, label + "：应拒绝"); }
        // 预期的本地拒绝：核对原因码。
        catch (SiyuRefusal refusal) { Check(refusal.Code == code, $"{label}：应是 {code}，实际 {refusal.Code}"); }
    }
}
