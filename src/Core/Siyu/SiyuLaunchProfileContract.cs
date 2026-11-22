using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Core.Siyu;

/// <summary>
/// 思域账户资料字段表（协议版本 1，对应中转机 Claude Code 2.1.325）的唯一登记处与核对入口。
/// 字段与形状取自中转机真实 oauthAccount 的只读类型快照；原生写 oauthAccount 时只有 accountUuid、emailAddress 必写，其余有才写，所以其余都不是必有。
/// 表外字段说明中转机上的 CLI 版本变了，拒绝建会话；不补默认值，null 原样是 null。
/// </summary>
public static class SiyuLaunchProfileContract
{
    /// <summary>oauthAccount 字段表，顺序即资料代次的计算顺序。</summary>
    public static IReadOnlyList<SiyuProfileField> OAuthAccountFields { get; } =
    [
        new("accountUuid", SiyuProfileValueKind.Uuid, Required: true, Nullable: false, InProfileVersion: true),
        new("emailAddress", SiyuProfileValueKind.Text, Required: true, Nullable: false, InProfileVersion: true),
        new("organizationUuid", SiyuProfileValueKind.Uuid, Required: false, Nullable: false, InProfileVersion: true),
        new("hasExtraUsageEnabled", SiyuProfileValueKind.Boolean, Required: false, Nullable: false, InProfileVersion: true),
        new("billingType", SiyuProfileValueKind.Text, Required: false, Nullable: false, InProfileVersion: true),
        new("accountCreatedAt", SiyuProfileValueKind.Timestamp, Required: false, Nullable: false, InProfileVersion: true),
        new("subscriptionCreatedAt", SiyuProfileValueKind.Timestamp, Required: false, Nullable: false, InProfileVersion: true),
        new("ccOnboardingFlags", SiyuProfileValueKind.Object, Required: false, Nullable: false, InProfileVersion: true),
        new("claudeCodeTrialEndsAt", SiyuProfileValueKind.UnverifiedScalar, Required: false, Nullable: true, InProfileVersion: true),
        new("claudeCodeTrialDurationDays", SiyuProfileValueKind.UnverifiedScalar, Required: false, Nullable: true, InProfileVersion: true),
        new("seatTier", SiyuProfileValueKind.UnverifiedScalar, Required: false, Nullable: true, InProfileVersion: true),
        new("planDisplayName", SiyuProfileValueKind.Text, Required: false, Nullable: false, InProfileVersion: true),
        new("displayName", SiyuProfileValueKind.Text, Required: false, Nullable: false, InProfileVersion: true),
        new("fullName", SiyuProfileValueKind.Text, Required: false, Nullable: false, InProfileVersion: true),
        new("profileFetchedAt", SiyuProfileValueKind.Number, Required: false, Nullable: false, InProfileVersion: false),
        new("organizationRole", SiyuProfileValueKind.Text, Required: false, Nullable: false, InProfileVersion: true),
        new("workspaceRole", SiyuProfileValueKind.UnverifiedScalar, Required: false, Nullable: true, InProfileVersion: true),
        new("organizationName", SiyuProfileValueKind.Text, Required: false, Nullable: false, InProfileVersion: true),
    ];

    /// <summary>
    /// 凭据 claudeAiOauth 里下发的三项：scopes 必有；subscriptionType、rateLimitTier 可缺可空。
    /// 观察到的 subscriptionTypeSource 不下发（真实验证轮没下发它，新启动、后台、恢复都通过）；令牌与期限永不下发。
    /// </summary>
    public static IReadOnlyList<SiyuProfileField> CredentialFields { get; } =
    [
        new("scopes", SiyuProfileValueKind.Text, Required: true, Nullable: false, InProfileVersion: true),
        new("subscriptionType", SiyuProfileValueKind.Text, Required: false, Nullable: true, InProfileVersion: true),
        new("rateLimitTier", SiyuProfileValueKind.Text, Required: false, Nullable: true, InProfileVersion: true),
    ];

    /// <summary>refusal 读句里字段名最长字符数，超出或含非字母数字的名字不读出。</summary>
    private const int MaxSpokenNameLength = 64;

    /// <summary>
    /// 核对 oauthAccount 原始字节（顶层 oauthAccount 的值）与凭据 claudeAiOauth 原始字节，合表就返回启动资料。
    /// 不合表抛 <see cref="SiyuRefusal"/>（503，原因码 profile-missing、profile-unverified 或 profile-malformed），读句说清下一步。
    /// </summary>
    public static SiyuLaunchProfile Read(ReadOnlySpan<byte> oauthAccount, ReadOnlySpan<byte> credential)
    {
        if (oauthAccount.IsEmpty) throw Refuse("profile-missing", "这个官方账户还没有账户资料。请在中转机管理页对这个账户获取一次模型，再启动思域。");
        using var account = Parse(oauthAccount, "oauthAccount");
        var oauth = Collect(account.RootElement, OAuthAccountFields, rejectUnknown: true, "oauthAccount");
        var accountUuid = oauth[0]!.Value.GetString()!;
        using var credentials = Parse(credential, "凭据");
        var metadata = Collect(credentials.RootElement, CredentialFields, rejectUnknown: false, "凭据");
        CheckScopes(metadata[0]!.Value);
        var credentialJson = WriteCredentialMetadata(metadata);
        var version = Version(oauth, metadata);
        return new SiyuLaunchProfile(accountUuid, oauthAccount.ToArray(), credentialJson, version);
    }

    /// <summary>解析成 JSON 对象；损坏或不是对象按 profile-malformed 拒绝。</summary>
    private static JsonDocument Parse(ReadOnlySpan<byte> bytes, string part)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes.ToArray());
        }
        // 损坏的资料文件不转述原文（可能含邮箱等），只报哪一部分坏了；拒绝在建会话之前，官方收不到任何请求。
        catch (JsonException)
        {
            throw Refuse("profile-malformed", $"中转机上这个官方账户的{part}已损坏。请在中转机管理页对这个账户获取一次模型，再启动思域。");
        }
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            throw Refuse("profile-malformed", $"中转机上这个官方账户的{part}不是预期的格式。请在中转机管理页对这个账户获取一次模型，再启动思域。");
        }
        return document;
    }

    /// <summary>按表取值：结果与表同序，缺的项为 null。重复键、类型不符、缺必有项一律拒绝；<paramref name="rejectUnknown"/> 时表外字段也拒绝。</summary>
    private static JsonElement?[] Collect(JsonElement root, IReadOnlyList<SiyuProfileField> table, bool rejectUnknown, string part)
    {
        var values = new JsonElement?[table.Count];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!seen.Add(property.Name)) throw Refuse("profile-malformed", $"中转机上这个官方账户的{part}里有重复的项「{Spoken(property.Name)}」。请在中转机管理页对这个账户获取一次模型，再启动思域。");
            var index = IndexOf(table, property.Name);
            if (index < 0)
            {
                if (rejectUnknown) throw Unverified(part, property.Name);
                continue;
            }
            if (!Fits(table[index], property.Value)) throw Unverified(part, property.Name);
            values[index] = property.Value;
        }
        for (var i = 0; i < table.Count; i++)
            if (table[i].Required && values[i] is null) throw Refuse("profile-missing", $"中转机上这个官方账户的{part}缺少「{table[i].Name}」。请在中转机管理页对这个账户获取一次模型，再启动思域。");
        return values;
    }

    /// <summary>字段表里的位置，找不到返回 -1。</summary>
    private static int IndexOf(IReadOnlyList<SiyuProfileField> table, string name)
    {
        for (var i = 0; i < table.Count; i++) if (string.Equals(table[i].Name, name, StringComparison.Ordinal)) return i;
        return -1;
    }

    /// <summary>值是否合这一行的形状。scopes 另由 <see cref="CheckScopes"/> 核对，这里只看是不是数组。</summary>
    private static bool Fits(SiyuProfileField field, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return field.Nullable;
        if (field.Name == "scopes") return value.ValueKind == JsonValueKind.Array;
        return field.Kind switch
        {
            SiyuProfileValueKind.Uuid => value.ValueKind == JsonValueKind.String && IsLowerUuid(value.GetString()!),
            SiyuProfileValueKind.Text => value.ValueKind == JsonValueKind.String,
            SiyuProfileValueKind.Timestamp => value.ValueKind == JsonValueKind.String && IsTimestamp(value.GetString()!),
            SiyuProfileValueKind.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            SiyuProfileValueKind.Number => value.ValueKind == JsonValueKind.Number,
            SiyuProfileValueKind.Object => value.ValueKind == JsonValueKind.Object,
            SiyuProfileValueKind.UnverifiedScalar => value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False,
            _ => false,
        };
    }

    /// <summary>小写连字符格式的 UUID。</summary>
    private static bool IsLowerUuid(string text) =>
        Guid.TryParseExact(text, "D", out _) && string.Equals(text, text.ToLowerInvariant(), StringComparison.Ordinal);

    /// <summary>带 T 分隔的 ISO 8601 日期时刻。</summary>
    private static bool IsTimestamp(string text) =>
        text.Length is >= 19 and <= 40 && text[10] == 'T' && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _);

    /// <summary>scopes 必须是非空数组，每项是非空字符串。</summary>
    private static void CheckScopes(JsonElement scopes)
    {
        if (scopes.GetArrayLength() == 0 || scopes.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String || item.GetString()!.Length == 0))
            throw Unverified("凭据", "scopes");
    }

    /// <summary>按凭据表的顺序写出只含存在项的 JSON 对象，值原样（规范化空白）。</summary>
    private static byte[] WriteCredentialMetadata(JsonElement?[] metadata)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            for (var i = 0; i < CredentialFields.Count; i++)
            {
                if (metadata[i] is not { } value) continue;
                writer.WritePropertyName(CredentialFields[i].Name);
                value.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    /// <summary>资料代次：按两张表的顺序，进代次的每一项写「名字、有无、规范化值」，SHA256 取前 16 个十六进制字符。</summary>
    private static string Version(JsonElement?[] oauth, JsonElement?[] metadata)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            Append(writer, OAuthAccountFields, oauth);
            Append(writer, CredentialFields, metadata);
            writer.WriteEndArray();
        }
        return Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()))[..16];
    }

    /// <summary>一张表的代次材料：每行一个 [名字, 有无, 值] 数组，不进代次的行跳过。</summary>
    private static void Append(Utf8JsonWriter writer, IReadOnlyList<SiyuProfileField> table, JsonElement?[] values)
    {
        for (var i = 0; i < table.Count; i++)
        {
            if (!table[i].InProfileVersion) continue;
            writer.WriteStartArray();
            writer.WriteStringValue(table[i].Name);
            writer.WriteBooleanValue(values[i] is not null);
            if (values[i] is { } value) value.WriteTo(writer); else writer.WriteNullValue();
            writer.WriteEndArray();
        }
    }

    /// <summary>表外字段或形状不符：中转机上的 CLI 写出了没核对过的资料。</summary>
    private static SiyuRefusal Unverified(string part, string name) =>
        Refuse("profile-unverified", $"中转机上这个官方账户的{part}里有没核对过的项「{Spoken(name)}」，思域暂不能用这个账户。请先在中转机完成 Claude Code 版本兼容验证。");

    /// <summary>读句里的字段名：只读出不长的字母数字名字。</summary>
    private static string Spoken(string name) =>
        name.Length is > 0 and <= MaxSpokenNameLength && name.All(char.IsAsciiLetterOrDigit) ? name : "（名字无法读出）";

    /// <summary>资料问题一律 503：官方收不到请求，也不进暂停冷却。</summary>
    private static SiyuRefusal Refuse(string code, string sentence) => new(503, code, sentence);
}
