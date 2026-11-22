using System.Text;
using Core.Siyu;
using static Tests.TestSuite;

namespace Tests;

/// <summary>
/// 思域协议版本 1 的固定合同：账户资料字段表、资料代次、会话数值、MCP 发现集合。
/// 资料样本的字段名、顺序与形状照中转机两个真实官方账户的只读类型快照（17 项与 18 项），取值全是合成的。
/// </summary>
public static class SiyuContractTests
{
    /// <summary>17 项的真实形状（无 planDisplayName）。</summary>
    private const string Account17 = """
        {
          "accountUuid": "0f6c2a1e-3b7d-4c55-9a10-2f4e8d9b1c01",
          "emailAddress": "someone@example.com",
          "organizationUuid": "7a1d3c5e-9b2f-4d8a-8c6e-1f3a5b7d9e02",
          "hasExtraUsageEnabled": false,
          "billingType": "stripe_subscription",
          "accountCreatedAt": "2025-03-04T05:06:07.123456Z",
          "subscriptionCreatedAt": "2025-03-05T05:06:07.654321Z",
          "ccOnboardingFlags": {},
          "claudeCodeTrialEndsAt": null,
          "claudeCodeTrialDurationDays": null,
          "seatTier": null,
          "displayName": "A",
          "fullName": "A",
          "profileFetchedAt": 1795327787407,
          "organizationRole": "admin",
          "workspaceRole": null,
          "organizationName": "someone@example.com's Organization"
        }
        """;

    /// <summary>凭据 claudeAiOauth 的真实形状，含不下发的秘密与期限。</summary>
    private const string Credential = """
        {"accessToken":"sk-ant-oat01-SECRET","refreshToken":"sk-ant-ort01-SECRET","expiresAt":1795330000000,"refreshTokenExpiresAt":1800000000000,
         "scopes":["user:file_upload","user:inference","user:mcp_servers","user:plugins","user:privacy_consents:write","user:profile","user:sessions:claude_code"],
         "subscriptionType":"max","subscriptionTypeSource":"profile","rateLimitTier":"default_claude_max_5x"}
        """;

    /// <summary>两种真实形状都能读；oauthAccount 原字节下发，凭据只出三项，令牌、期限、subscriptionTypeSource 都不出。</summary>
    public static Task ProfileTableReadsRealShapes()
    {
        var profile = Read(Account17, Credential);
        Check(profile.AccountUuid == "0f6c2a1e-3b7d-4c55-9a10-2f4e8d9b1c01", "账户编号应取 accountUuid");
        Check(profile.OAuthAccountJson.Span.SequenceEqual(Encoding.UTF8.GetBytes(Account17)), "oauthAccount 应原字节下发");
        var metadata = Encoding.UTF8.GetString(profile.CredentialMetadataJson.Span);
        Check(metadata == """{"scopes":["user:file_upload","user:inference","user:mcp_servers","user:plugins","user:privacy_consents:write","user:profile","user:sessions:claude_code"],"subscriptionType":"max","rateLimitTier":"default_claude_max_5x"}""",
            "凭据资料应只有三项、按表的顺序：" + metadata);
        Check(profile.ProfileVersion.Length == 16 && profile.ProfileVersion.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'), "资料代次应是 16 个小写十六进制字符");
        var account18 = Account17.Replace("\"seatTier\": null,", "\"seatTier\": null,\n  \"planDisplayName\": \"Max 5\",", StringComparison.Ordinal);
        Check(Read(account18, Credential).AccountUuid == profile.AccountUuid, "多 planDisplayName 的 18 项形状应能读");
        var minimal = """{"accountUuid":"0f6c2a1e-3b7d-4c55-9a10-2f4e8d9b1c01","emailAddress":"someone@example.com","profileFetchedAt":1}""";
        Check(Read(minimal, """{"scopes":["user:inference"]}""").CredentialMetadataJson.Span.SequenceEqual("""{"scopes":["user:inference"]}"""u8), "可缺项缺了不补默认值");
        Check(Encoding.UTF8.GetString(Read(Account17, """{"scopes":["user:inference"],"subscriptionType":null}""").CredentialMetadataJson.Span) == """{"scopes":["user:inference"],"subscriptionType":null}""", "null 原样是 null");
        Check(Read(Account17.Replace("\"seatTier\": null", "\"seatTier\": \"standard\"", StringComparison.Ordinal), Credential).AccountUuid.Length == 36, "类型未明的项非空标量原样放行");
        return Task.CompletedTask;
    }

    /// <summary>缺资料、表外字段、类型不符、重复键、损坏一律 503 本地拒绝，读句不转述资料原文。</summary>
    public static Task ProfileRefusals()
    {
        void Refused(string account, string credential, string code, string label)
        {
            try
            {
                SiyuLaunchProfileContract.Read(Encoding.UTF8.GetBytes(account), Encoding.UTF8.GetBytes(credential));
                Check(false, label + "：应拒绝");
            }
            // 预期的拒绝：核对状态码、原因码，并确认读句不带邮箱与令牌原文。
            catch (SiyuRefusal refusal)
            {
                Check(refusal.Status == 503 && refusal.Code == code, $"{label}：应是 503 {code}，实际 {refusal.Status} {refusal.Code}");
                Check(!refusal.Sentence.Contains("someone@", StringComparison.Ordinal) && !refusal.Sentence.Contains("SECRET", StringComparison.Ordinal), label + "：读句不应转述资料原文");
            }
        }
        string Swap(string from, string to) => Account17.Replace(from, to, StringComparison.Ordinal);
        Refused("", Credential, "profile-missing", "没有 oauthAccount");
        Refused("""{"accountUuid":"0f6c2a1e-3b7d-4c55-9a10-2f4e8d9b1c01","emailAddress":"someone@example.com","organizationUuid":"7a1d3c5e-9b2f-4d8a-8c6e-1f3a5b7d9e02"}""",
            Credential, "profile-incomplete", "中转旧接法只补的三项");
        Refused(Swap("\"profileFetchedAt\": 1795327787407,", ""), Credential, "profile-incomplete", "缺 profileFetchedAt");
        Refused(Swap("\"emailAddress\": \"someone@example.com\",", ""), Credential, "profile-missing", "缺 emailAddress");
        Refused(Account17, """{"accessToken":"x"}""", "profile-missing", "缺 scopes");
        Refused(Swap("\"seatTier\": null,", "\"seatTier\": null,\n  \"newField\": 1,"), Credential, "profile-unverified", "表外字段");
        Refused(Swap("0f6c2a1e-3b7d-4c55-9a10-2f4e8d9b1c01", "0F6C2A1E-3B7D-4C55-9A10-2F4E8D9B1C01"), Credential, "profile-unverified", "大写 UUID");
        Refused(Swap("\"hasExtraUsageEnabled\": false", "\"hasExtraUsageEnabled\": \"false\""), Credential, "profile-unverified", "布尔写成字符串");
        Refused(Swap("2025-03-04T05:06:07.123456Z", "2025-03-04"), Credential, "profile-unverified", "时间不是 ISO 日期时刻");
        Refused(Swap("\"ccOnboardingFlags\": {}", "\"ccOnboardingFlags\": []"), Credential, "profile-unverified", "对象写成数组");
        Refused(Swap("\"seatTier\": null", "\"seatTier\": {}"), Credential, "profile-unverified", "类型未明的项给了对象");
        Refused(Swap("\"billingType\": \"stripe_subscription\"", "\"billingType\": null"), Credential, "profile-unverified", "不可空的项给了 null");
        Refused(Swap("\"displayName\": \"A\",", "\"displayName\": \"A\",\n  \"displayName\": \"B\","), Credential, "profile-malformed", "重复键");
        Refused("{\"accountUuid\":", Credential, "profile-malformed", "损坏");
        Refused("[]", Credential, "profile-malformed", "不是对象");
        Refused(Account17, """{"scopes":[]}""", "profile-unverified", "scopes 为空");
        Refused(Account17, """{"scopes":["user:inference",""]}""", "profile-unverified", "scopes 含空串");
        Refused(Account17, """{"scopes":"user:inference"}""", "profile-unverified", "scopes 不是数组");
        Refused(Account17, "not json", "profile-malformed", "凭据损坏");
        return Task.CompletedTask;
    }

    /// <summary>资料代次只随进代次的项变：profileFetchedAt、空白、令牌、期限、subscriptionTypeSource 变了不换代，其余任何一项变了都换代。</summary>
    public static Task ProfileVersionTracksRelevantChanges()
    {
        var baseline = Read(Account17, Credential).ProfileVersion;
        void Same(string account, string credential, string label) => Check(Read(account, credential).ProfileVersion == baseline, label + "：不应换代");
        void Different(string account, string credential, string label) => Check(Read(account, credential).ProfileVersion != baseline, label + "：应换代");
        Same(Account17.Replace("1795327787407", "1795999999999", StringComparison.Ordinal), Credential, "只改取资料时间");
        Same(Account17.Replace("\n", "", StringComparison.Ordinal).Replace("  ", "", StringComparison.Ordinal), Credential, "只改空白");
        Same(Account17, Credential.Replace("sk-ant-oat01-SECRET", "sk-ant-oat01-OTHER", StringComparison.Ordinal).Replace("1795330000000", "1795339999999", StringComparison.Ordinal), "只换官方令牌与期限");
        Same(Account17, Credential.Replace("\"profile\"", "\"token\"", StringComparison.Ordinal), "只改 subscriptionTypeSource");
        Different(Account17.Replace("stripe_subscription", "apple_subscription", StringComparison.Ordinal), Credential, "billingType");
        Different(Account17.Replace("\"organizationRole\": \"admin\"", "\"organizationRole\": \"user\"", StringComparison.Ordinal), Credential, "organizationRole");
        Different(Account17.Replace("\"ccOnboardingFlags\": {}", "\"ccOnboardingFlags\": {\"x\": true}", StringComparison.Ordinal), Credential, "ccOnboardingFlags 内容");
        Different(Account17.Replace("\"seatTier\": null,", "\"seatTier\": null,\n  \"planDisplayName\": \"Max 5\",", StringComparison.Ordinal), Credential, "多一项 planDisplayName");
        Different(Account17.Replace("\"workspaceRole\": null,", "", StringComparison.Ordinal), Credential, "null 项变成缺项");
        Different(Account17, Credential.Replace("\"subscriptionType\":\"max\"", "\"subscriptionType\":\"pro\"", StringComparison.Ordinal), "subscriptionType");
        Different(Account17, Credential.Replace("default_claude_max_5x", "default_claude_max_20x", StringComparison.Ordinal), "rateLimitTier");
        Different(Account17, Credential.Replace("\"user:file_upload\",", "", StringComparison.Ordinal), "scopes 少一项");
        return Task.CompletedTask;
    }

    /// <summary>版本 1 数值之间的关系：续租留两次余量，隧道上限盖住实测，保留期不短于租约，发现限时短于有效期。</summary>
    public static Task ContractValuesHang()
    {
        Check(SiyuContract.ProtocolVersion == 1 && SiyuContract.RequestStrategy == "nativePreserve" && SiyuContract.Transport == "nativeHttpsProxy", "版本 1 只有一种策略一种传输");
        Check(SiyuContract.LeaseRenewAfter * 3 <= SiyuContract.LeaseDuration && SiyuContract.LeaseRenewAfter > TimeSpan.Zero, "续租时刻应留出两次错过的余量");
        Check(SiyuContract.MaxTunnelsPerSession >= 9 && SiyuContract.MaxSessionsPerUser >= 1, "隧道上限应盖住实测的一整趟 9 条");
        Check(SiyuContract.CreateReplayRetention >= SiyuContract.LeaseDuration && SiyuContract.ClosedRetention >= SiyuContract.LeaseDuration, "保留期不短于租约");
        Check(SiyuContract.McpDiscoveryTimeout < SiyuContract.McpDiscoveryLifetime, "发现限时应短于集合有效期");
        Check(SiyuLaunchProfileContract.OAuthAccountFields.Count == 18 && SiyuLaunchProfileContract.OAuthAccountFields.Select(f => f.Name).Distinct(StringComparer.Ordinal).Count() == 18, "字段表 18 项且不重名");
        Check(SiyuLaunchProfileContract.OAuthAccountFields.Single(f => !f.InProfileVersion).Name == "profileFetchedAt", "只有 profileFetchedAt 不进代次");
        Check(SiyuLaunchProfileContract.OAuthAccountFields.Where(f => f.Required).Select(f => f.Name).SequenceEqual(["accountUuid", "emailAddress"]), "必有项只有 accountUuid、emailAddress");
        return Task.CompletedTask;
    }

    /// <summary>发现集合：键是账户与资料代次，到点过期，迟到的旧发现不覆盖新的、不复活已失效的，去掉一个连接器不动其余与期限。</summary>
    public static Task McpDiscoveryLifecycle()
    {
        var start = new DateTimeOffset(2026, 11, 22, 0, 0, 0, TimeSpan.Zero);
        var clock = new UsageClock(start);
        var registry = new McpDiscoveryRegistry(clock);
        Check(registry.Check(1, "v1", "mcpsrv_a") == McpDiscoveryCheck.NotDiscovered, "没发现过应是 NotDiscovered");
        Check(registry.Replace(1, "v1", ["mcpsrv_a", "mcpsrv_b", "", "mcpsrv_a"], start), "成功发现应写入");
        Check(registry.Check(1, "v1", "mcpsrv_a") == McpDiscoveryCheck.Permitted && registry.Check(1, "v1", "mcpsrv_b") == McpDiscoveryCheck.Permitted, "集合里的连接器应放行");
        Check(registry.Check(1, "v1", "mcpsrv_c") == McpDiscoveryCheck.Unknown, "集合外的连接器应是 Unknown");
        Check(registry.Check(1, "v2", "mcpsrv_a") == McpDiscoveryCheck.NotDiscovered, "资料换代后旧集合不算");
        Check(registry.Check(2, "v1", "mcpsrv_a") == McpDiscoveryCheck.NotDiscovered, "别的账户不能用这份集合");

        clock.Set(start + SiyuContract.McpDiscoveryLifetime - TimeSpan.FromTicks(1));
        Check(registry.Check(1, "v1", "mcpsrv_a") == McpDiscoveryCheck.Permitted, "到期前一刻仍放行");
        clock.Set(start + SiyuContract.McpDiscoveryLifetime);
        Check(registry.Check(1, "v1", "mcpsrv_a") == McpDiscoveryCheck.NotDiscovered, "到期那一刻起不放行");

        var second = start + TimeSpan.FromMinutes(11);
        clock.Set(second);
        Check(registry.Replace(1, "v1", ["mcpsrv_b"], second), "新发现整份替换");
        Check(registry.Check(1, "v1", "mcpsrv_a") == McpDiscoveryCheck.Unknown, "替换后旧连接器不再放行");
        Check(!registry.Replace(1, "v1", ["mcpsrv_a"], second - TimeSpan.FromSeconds(1)), "比现有集合早开始的发现不应覆盖");
        Check(registry.Check(1, "v1", "mcpsrv_a") == McpDiscoveryCheck.Unknown, "迟到的旧发现没有新增许可");

        Check(registry.Replace(1, "v1", ["mcpsrv_b", "mcpsrv_c"], second), "同一时刻开始的发现可以替换");
        registry.Forget(1, "mcpsrv_b");
        Check(registry.Check(1, "v1", "mcpsrv_b") == McpDiscoveryCheck.Unknown && registry.Check(1, "v1", "mcpsrv_c") == McpDiscoveryCheck.Permitted, "去掉一个连接器不动其余");
        clock.Set(second + SiyuContract.McpDiscoveryLifetime);
        Check(registry.Check(1, "v1", "mcpsrv_c") == McpDiscoveryCheck.NotDiscovered, "去掉连接器不延长有效期");

        var third = second + TimeSpan.FromMinutes(20);
        clock.Set(third);
        Check(registry.Replace(1, "v1", ["mcpsrv_c"], third), "重新发现");
        var inflight = third + TimeSpan.FromSeconds(1);
        clock.Set(third + TimeSpan.FromSeconds(2));
        registry.Invalidate(1);
        Check(registry.Check(1, "v1", "mcpsrv_c") == McpDiscoveryCheck.NotDiscovered, "失效后不放行");
        Check(!registry.Replace(1, "v1", ["mcpsrv_c"], inflight), "失效前开始的迟到发现不应复活许可");
        Check(registry.Check(1, "v1", "mcpsrv_c") == McpDiscoveryCheck.NotDiscovered, "迟到发现被挡下后仍不放行");
        clock.Set(third + TimeSpan.FromSeconds(3));
        Check(registry.Replace(1, "v1", ["mcpsrv_c"], clock.GetUtcNow()), "失效之后开始的发现可以写入");
        Check(registry.Check(1, "v1", "mcpsrv_c") == McpDiscoveryCheck.Permitted, "失效后重新发现即可放行");
        return Task.CompletedTask;
    }

    /// <summary>按字符串读资料。</summary>
    private static SiyuLaunchProfile Read(string account, string credential) =>
        SiyuLaunchProfileContract.Read(Encoding.UTF8.GetBytes(account), Encoding.UTF8.GetBytes(credential));
}
