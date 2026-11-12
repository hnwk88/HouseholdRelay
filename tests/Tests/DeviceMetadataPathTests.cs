using System.Security.Cryptography;
using System.Text;
using System.Collections.Concurrent;
using System.Text.Json;
using static Tests.TestSuite;

namespace Tests;

/// <summary>两种设备格式经完整业务与真实探测实现到达回环模拟官方的矩阵；CLI 使用独立假进程。</summary>
public static class DeviceMetadataPathTests
{
    /// <summary>关闭兼容归一选项后，四个业务与四个探测样本仍统一设备；八类业务反例均不出站。</summary>
    public static async Task BusinessAndProbeMatrix()
    {
        using var claude = new FakeClaudeLauncher("success");
        await using var mock = await UpstreamFixture.Create();
        var logs = new ConcurrentQueue<string>();
        await using var web = await WebFixture.Create(configuration => configuration.NormalizeDeviceMetadata = false, services =>
        { claude.Register(services); services.AddSingleton<IRelayEventLog>(new FixtureLog(logs)); });
        var settings = web.Settings.Read(); settings.Value.NormalizeDeviceMetadata = false; web.Settings.Save(settings.Version, settings.Value);
        var options = mock.Options(mode: "oauth", authentication: "bearer") with { OfficialLogin = true };
        var candidate = await web.Upstreams.ImportOfficial(web.Upstreams.ReadActive().Version, options, FakeClaude.Credential("fixture-protocol-token", 60), "模拟官方", CancellationToken.None);
        var report = await web.Upstreams.TestCandidate(candidate, true, CancellationToken.None);
        Check(report.CanActivate, report.Message); web.Upstreams.Activate(candidate);
        var token = web.Identities.Create("矩阵客户端").Token;
        var canonical = web.Settings.Read().Value.DeviceId;
        var generation = web.App.Services.GetRequiredService<ISiteIdentityService>().Current.Version;
        web.App.Services.GetRequiredService<IProtocolDiagnostics>().Start();
        var first = mock.Calls.Count;
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        var sequence = 0;
        foreach (var embedded in new[] { false, true })
        foreach (var digit in new[] { 'a', 'b' })
        {
            var downstream = new string(digit, 64);
            var original = Sample(embedded, downstream, "fixture-model", claude.Version, "sdk-cli");
            var expected = original.Replace(downstream, canonical, StringComparison.Ordinal);
            using (var request = UsageFixture.Request(web, token, "metadata-matrix", body: original))
            using (var response = await web.Http.SendAsync(request))
                Check(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            AssertSample(mock.Calls.Last(), expected, downstream, canonical, hashes);
            claude.Reinstall("metadata-" + (embedded ? "json-" : "legacy-") + digit, claude.Version);
            using (var transport = new UpstreamConnection(new(1, options, "fixture-protocol-token"), web.App.Services.GetRequiredService<IUpstreamProxy>()))
            {
                var identity = ClaudeCodeIdentity.From(FakeClaude.Identity(claude.Version), claude.Version, "sdk-cli", DateTimeOffset.UnixEpoch);
                var reply = await web.App.Services.GetRequiredService<IClaudeCodeProbe>().Run(transport, identity, CancellationToken.None);
                Check((int)reply.Status == 200, "The actual probe implementation must forward one valid sample.");
            }
            AssertSample(mock.Calls.Last(), expected, downstream, canonical, hashes);
            sequence += 2;
            Check(mock.Calls.Count == first + sequence, "Each business or probe sample sends exactly once.");
        }
        Check(sequence == 8 && hashes.Count == 1 && !web.Settings.Read().Value.NormalizeDeviceMetadata, "The eight-line matrix must use one device even with compatibility normalization disabled.");
        var legacy = JsonSerializer.Serialize("user_" + new string('a', 64) + "_account_account_session_session");
        var duplicateDevice = JsonSerializer.Serialize("{\"device_id\":\"a\",\"device_id\":\"b\"}");
        string[] invalid =
        [
            "{\"model\":\"fixture-model\",\"messages\":[]}",
            Bad("null"), Bad("42"), Bad("\"unknown-version\""), Bad(JsonSerializer.Serialize("{broken")),
            "{\"model\":\"fixture-model\",\"metadata\":{\"user_id\":" + legacy + "},\"metadata\":{\"user_id\":" + legacy + "}}",
            "{\"model\":\"fixture-model\",\"metadata\":{\"user_id\":" + legacy + ",\"user_id\":" + legacy + "}}",
            Bad(duplicateDevice)
        ];
        var allowed = mock.Calls.Count;
        foreach (var input in invalid)
        {
            using var request = UsageFixture.Request(web, token, "metadata-rejected", body: input);
            using var response = await web.Http.SendAsync(request);
            Check((int)response.StatusCode == 400, "Unsupported official metadata must be a local 400.");
            Check(mock.Calls.Count == allowed, "Each negative category must produce zero upstream requests.");
        }
        using var lease = web.Relay.Upstreams.Acquire();
        Check(!lease.Paused && lease.CooldownSeconds == 0, "Local failures do not change upstream authentication or cooldown.");
        var records = logs.Select(line => JsonSerializer.Deserialize<JsonElement>(line))
            .Where(entry => entry.TryGetProperty("diagnostic", out var kind) && kind.GetString() == "site_identity").ToArray();
        var groups = records.GroupBy(entry => entry.GetProperty("correlation").GetString()).ToArray();
        Check(records.Length == 32 && groups.Length == 16, "Business/probe success has three observed stages, local rejection only one; header captures do not pretend to be official traffic.");
        Check(groups.Count(group => group.Select(entry => entry.GetProperty("stage").GetString()).SequenceEqual(["assembled", "dispatch", "response"])) == 8,
            "All four business and four probe requests must preserve stage order.");
        Check(groups.Count(group => group.Select(entry => entry.GetProperty("stage").GetString()).SequenceEqual(["local_rejection"])) == 8,
            "The eight local refusals must not claim dispatch or response.");
        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        Check(records.All(entry => entry.GetProperty("generation").GetInt64() == generation && entry.GetProperty("deviceHash").GetString() == expectedHash),
            "Every event must refer to the actual operation identity generation.");
        Check(records.All(entry => !entry.GetRawText().Contains(canonical, StringComparison.Ordinal) && !entry.GetRawText().Contains("fixture-protocol-token", StringComparison.Ordinal)
            && !entry.GetRawText().Contains(token, StringComparison.Ordinal) && !entry.GetRawText().Contains("Reply with OK.", StringComparison.Ordinal)), "Diagnostic samples contain neither credentials nor original body/device values.");
        Console.WriteLine("Protocol matrix: 8 allowed wire samples, 1 device hash, 0 downstream device occurrences, 0 changes outside device bytes; 8 rejected categories, 0 upstream requests; 32 stage events at one generation.");
    }

    /// <summary>构造由假 CLI 和业务客户端共用的确定正文；内嵌 JSON 保留空白、转义、未知扩展和属性顺序。</summary>
    internal static string Sample(bool embedded, string device, string model, string version, string entrypoint)
    {
        var identifier = embedded ? "{ \"session_id\" : \"sess\\u0069on\", \"future\" : [1,{\"x\":\"a\\/b\"}], \"device_id\" : \"" + device + "\", \"account_uuid\" : \"account\" }"
            : "user_" + device + "_account_account_session_session";
        return "{ \"model\":" + JsonSerializer.Serialize(model) + ", \"system\" : [{\"type\":\"text\",\"text\":"
            + JsonSerializer.Serialize($"x-anthropic-billing-header: cc_version={version}.abc; cc_entrypoint={entrypoint}; conversation_fingerprint=fixture;")
            + "},{\"type\":\"text\",\"text\":\"fixture system\"}], \"max_tokens\":16, \"stream\":false, \"metadata\":{\"user_id\":" + JsonSerializer.Serialize(identifier)
            + "}, \"messages\":[{\"role\":\"user\",\"content\":\"Reply with OK.\"}], \"tools\":[], \"extension\":{\"unchanged\":true} }";
    }

    /// <summary>检查回环服务器实际保存的出站正文；整个字节序列等于仅替换设备值的预期，且只收集设备哈希。</summary>
    private static void AssertSample(UpstreamCall call, string expected, string downstream, string canonical, ISet<string> hashes)
    {
        Check(Encoding.UTF8.GetBytes(call.Body).SequenceEqual(Encoding.UTF8.GetBytes(expected)), "Non-device bytes, billing content, account and session must remain verbatim on the wire.");
        Check(!call.Body.Contains(downstream, StringComparison.Ordinal), "The original client device must never leave.");
        using var outer = JsonDocument.Parse(call.Body);
        var identifier = outer.RootElement.GetProperty("metadata").GetProperty("user_id").GetString()!;
        string device;
        if (identifier.StartsWith("user_", StringComparison.Ordinal)) device = identifier.Substring(5, 64);
        else { using var inner = JsonDocument.Parse(identifier); device = inner.RootElement.GetProperty("device_id").GetString()!; }
        Check(device == canonical);
        hashes.Add(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(device))));
    }

    /// <summary>把待拒绝的 user_id 值放在其他字段有效的 Messages 请求中，隔离设备规则的失败原因。</summary>
    private static string Bad(string identifier) => "{\"model\":\"fixture-model\",\"messages\":[],\"metadata\":{\"user_id\":" + identifier + "}}";
}
