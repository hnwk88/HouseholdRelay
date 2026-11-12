using System.Text;
using System.Text.Json;
using Infrastructure.ExternalSources.Network;
using static Tests.TestSuite;

namespace Tests;

/// <summary>官方共同出站设备规则的字节保真、失败关闭及端点隔离验证；只访问本机模拟上游。</summary>
public static class DeviceMetadataTests
{
    /// <summary>本组测试的站点编号；与所有模拟下游设备编号不同。</summary>
    private static readonly string Canonical = new('c', 64);

    /// <summary>两种设备格式及转义变体都只改设备值；同号重写复用原数组，会话、账户和归因原字节保持。</summary>
    public static async Task OfficialBytesRemainVerbatim()
    {
        await using var mock = await UpstreamFixture.Create();
        using var transport = Connection(mock);
        var identity = Identity();
        foreach (var (template, original) in Supported())
        {
            var input = Encoding.UTF8.GetBytes(template.Replace("DEVICE_SEGMENT", original, StringComparison.Ordinal));
            var expected = Encoding.UTF8.GetBytes(template.Replace("DEVICE_SEGMENT", Canonical, StringComparison.Ordinal));
            var normalized = DeviceMetadata.ForOfficial("/v1/messages", input, Canonical);
            Check(normalized.SequenceEqual(expected), "Only the device value bytes may change, including nested JSON escapes.");
            Check(ReferenceEquals(normalized, DeviceMetadata.ForOfficial("/v1/messages", normalized, Canonical)), "The current device identity needs no second copy.");
            using var request = transport.Request("/v1/messages", "?beta=true", input, "POST", "unused", identity: identity, deviceId: Canonical);
            using var response = await transport.Send(request, CancellationToken.None);
            Check(response.IsSuccessStatusCode && Encoding.UTF8.GetBytes(mock.Calls.Last().Body).SequenceEqual(expected), "The common outbound boundary must apply the same byte-preserving rule.");
        }
        Check(mock.Calls.Count == Supported().Count(), "Each allowed sample must send exactly once.");
    }

    /// <summary>缺失、损坏、未知格式及后置重复键在共同出口拒绝；模拟官方收到零次请求且不进入暂停或冷却。</summary>
    public static async Task OfficialInvalidMetadataNeverDispatches()
    {
        await using var mock = await UpstreamFixture.Create();
        using var transport = Connection(mock);
        var valid = JsonSerializer.Serialize("user_" + new string('a', 64) + "_account_account_session_session");
        var innerDuplicate = JsonSerializer.Serialize("{\"device_id\":\"old\",\"device_id\":\"other\"}");
        var innerEscapedDuplicate = JsonSerializer.Serialize("{\"device_id\":\"old\",\"device_\\u0069d\":\"other\"}");
        string[] rejected =
        [
            "{}", "{\"metadata\":null}", "{\"metadata\":{}}", "{\"metadata\":{\"user_id\":null}}",
            "{\"metadata\":{\"user_id\":12}}", "{\"metadata\":{\"user_id\":\"\"}}",
            Wrap(JsonSerializer.Serialize("unknown-future-format")), Wrap(JsonSerializer.Serialize("{broken-json")),
            "{\"metadata\":{\"user_id\":" + valid + "},\"metadata\":{\"user_id\":" + valid + "}}",
            "{\"metadata\":{\"user_id\":" + valid + ",\"user_id\":" + valid + "}}",
            "{\"metadata\":{\"user_id\":" + valid + "},\"meta\\u0064ata\":{\"user_id\":" + valid + "}}",
            "{\"metadata\":{\"user_id\":" + valid + ",\"user_\\u0069d\":" + valid + "}}",
            Wrap(innerDuplicate), Wrap(innerEscapedDuplicate), Wrap(JsonSerializer.Serialize("{\"device_id\":null}")),
            Wrap(JsonSerializer.Serialize("{\"device_id\":\"\"}")), Wrap(JsonSerializer.Serialize("{\"session_id\":\"s\"}")),
            "{\"metadata\":{\"user_id\":" + valid + "},\"after\":[}",
            Wrap(JsonSerializer.Serialize("{\"device_id\":\"old\"} trailing")), "[]",
            Wrap(JsonSerializer.Serialize("{\"device_id\":\"old\",\"after\":[}")),
            "{\"metadata\":{\"user_id\":\"\\ud800\"}}"
        ];
        foreach (var body in rejected)
        {
            try
            {
                using var request = transport.Request("/v1/messages", "", Encoding.UTF8.GetBytes(body), "POST", "unused", identity: Identity(), deviceId: Canonical);
                using var response = await transport.Send(request, CancellationToken.None);
                Check(false, "Unsupported metadata must fail locally before dispatch.");
            }
            // 只接受固定的本地协议拒绝，网络、解析器或其他异常都必须让测试失败。
            catch (RelayFailure error)
            {
                Check(error.Status == 400 && error.RetryAfter == 0 && !error.Message.Contains("old", StringComparison.Ordinal), "A fixed local rejection must not quote input or request authentication recovery.");
            }
            transport.CheckState();
            Check(!transport.Paused && transport.CooldownSeconds == 0, "Local metadata rejection must not pause or cool down the upstream.");
        }
        Check(mock.Calls.IsEmpty, "No invalid sample may reach the mock official upstream.");
    }

    /// <summary>models 空体与无 metadata 的 count_tokens 可用；官方端点扩展及缺编号拒绝，API 和兼容上游正文保持。</summary>
    public static async Task OfficialEndpointContracts()
    {
        await using var mock = await UpstreamFixture.Create();
        var available = false;
        using (var held = new UpstreamConnection(new(1, mock.Options(mode: "oauth", authentication: "bearer") with { OfficialLogin = true }, "fixture-secret"), Proxy(), officialAvailable: () => available))
        {
            Check(!held.Usable, "Official maintenance closes new route selection.");
            try { held.CheckState(); Check(false, "Official maintenance must reject admission."); }
            // 维护只提供固定本地 503，不代表认证暂停或上游限流。
            catch (RelayFailure error) { Check(error.Status == 503); }
            available = true; held.CheckState(); Check(held.Usable);
            using var unauthorized = new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized);
            using var limited = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
            held.Observe(unauthorized); held.Observe(limited);
            available = false; Check(!held.Usable);
            available = true;
            Check(held.Paused && held.CooldownSeconds > 0 && !held.Usable, "Finishing maintenance cannot reset authentication pauses or cooldown.");
        }
        using (var api = new UpstreamConnection(new(1, mock.Options(), "fixture-secret"), Proxy(), officialAvailable: () => false))
        { api.CheckState(); Check(api.Usable, "Official maintenance does not close independent API admission."); }
        using var official = Connection(mock);
        var count = Encoding.UTF8.GetBytes("{ \"model\":\"fixture-model\", \"messages\":[], \"tools\":[], \"extension\":{\"metadata\":\"nested-business-data\"} }");
        using (var models = official.Request("/v1/models", "", [], "GET", "unused", identity: Identity(), deviceId: Canonical))
        using (var response = await official.Send(models, CancellationToken.None)) Check(response.IsSuccessStatusCode);
        using (var request = official.Request("/v1/messages/count_tokens", "", count, "POST", "unused", identity: Identity(), deviceId: Canonical))
        using (var response = await official.Send(request, CancellationToken.None))
            Check(response.IsSuccessStatusCode && Encoding.UTF8.GetBytes(mock.Calls.Last().Body).SequenceEqual(count), "Count requests without top-level metadata remain byte-identical.");
        Reject(() => official.Request("/v1/models", "", "{}"u8.ToArray(), "GET", "unused", identity: Identity(), deviceId: Canonical), 400);
        Reject(() => official.Request("/v1/messages/count_tokens", "", "{\"metadata\":null}"u8.ToArray(), "POST", "unused", identity: Identity(), deviceId: Canonical), 400);
        Reject(() => official.Request("/v1/messages/count_tokens", "", "{\"meta\\u0064ata\":{}}"u8.ToArray(), "POST", "unused", identity: Identity(), deviceId: Canonical), 400);
        Reject(() => official.Request("/v1/messages/count_tokens", "", "[]"u8.ToArray(), "POST", "unused", identity: Identity(), deviceId: Canonical), 400);
        Reject(() => official.Request("/v1/models", "", [], "GET", "unused", identity: Identity()), 503);
        Reject(() => official.Request("/v1/models", "", [], "GET", "unused", identity: Identity(), deviceId: "invalid"), 503);
        Check(mock.Calls.Count == 2 && !official.Paused && official.CooldownSeconds == 0, "Rejected endpoint variants do not send or change upstream state.");
        foreach (var mode in new[] { "api-key", "oauth" })
        {
            using var other = new UpstreamConnection(new(1, mock.Options(mode: mode, authentication: mode == "oauth" ? "bearer" : "x-api-key"), "fixture-secret"), Proxy());
            foreach (var body in new[] { "{}", "{\"metadata\":{\"user_id\":\"unknown\"}}", "{\"metadata\":null}", "{\"metadata\":{},\"metadata\":{}}" })
            {
                var bytes = Encoding.UTF8.GetBytes(body);
                using var request = other.Request("/v1/messages", "", bytes, "POST", "fixture-agent", deviceId: "ignored");
                using var response = await other.Send(request, CancellationToken.None);
                Check(response.IsSuccessStatusCode && Encoding.UTF8.GetBytes(mock.Calls.Last().Body).SequenceEqual(bytes), "Independent API and compatible OAuth bypass the official metadata policy.");
            }
        }
    }

    /// <summary>构造完整业务正文，归因、顺序、格式及设备以外的内容必须逐字保留。</summary>
    private static string Wrap(string identifier) => "{ \"system\" : [{\"type\":\"text\",\"text\":\"x-anthropic-billing-header: cc_version=1.0.0.abc; cc_entrypoint=cli; fingerprint=unchanged;\"}], \"metadata\" : {\"user_id\":" + identifier + ",\"extension\":true}, \"messages\" : [{\"role\":\"user\",\"content\":\"unchanged prompt\"}], \"tools\": [] }";

    /// <summary>覆盖旧格式和内嵌 JSON 的普通、Unicode 及双层转义；嵌套同名扩展不是目标字段。</summary>
    private static IEnumerable<(string Template, string Original)> Supported()
    {
        foreach (var digit in new[] { 'a', 'b' })
        {
            var device = new string(digit, 64);
            yield return (Wrap("\"user_DEVICE_SEGMENT_account_account_session_session\""), device);
            yield return (Wrap("\"\\u0075ser_DEVICE_SEGMENT_account_\\u0061ccount_session_session\""), string.Concat(Enumerable.Repeat($"\\u00{(int)digit:x2}", 64)));
            var inner = "{ \"extension\" : [\"中😀\", \"a\\/b\", {\"device_id\":\"nested-unchanged\"}], \"session_id\" : \"s\\u0065ssion\", \"device_id\" : \"DEVICE_SEGMENT\", \"account_uuid\" : \"account\" }";
            var serialized = JsonSerializer.Serialize(inner);
            yield return (Wrap(serialized), device);
            yield return (Wrap(serialized), JsonSerializer.Serialize(string.Concat(Enumerable.Repeat($"\\u00{(int)digit:x2}", 64)))[1..^1]);
            yield return (Wrap(serialized.Replace("\\u4E2D", "中", StringComparison.Ordinal).Replace("\\uD83D\\uDE00", "😀", StringComparison.Ordinal)), device);
        }
    }

    /// <summary>创建仅访问回环的官方租约；假凭据和假客户端身份不接触真实账户。</summary>
    private static UpstreamConnection Connection(UpstreamFixture mock) => new(new(1,
        mock.Options(mode: "oauth", authentication: "bearer") with { OfficialLogin = true }, "fixture-secret"), Proxy());

    /// <summary>固定无代理设置；本组请求目标只能来自本机模拟上游。</summary>
    private static IUpstreamProxy Proxy() => new SystemUpstreamProxy(() => new RelayConfiguration { ProxyMode = "none" });

    /// <summary>取假 CLI 的已知稳定身份，避免运行真 CLI 或加载个人配置。</summary>
    private static ClaudeCodeIdentity Identity() => ClaudeCodeIdentity.From(FakeClaude.Identity("1.0.0"), "1.0.0", "sdk-cli", DateTimeOffset.UnixEpoch);

    /// <summary>验证构造阶段的固定本地错误；意外成功时仍释放请求，不产生网络发送。</summary>
    private static void Reject(Func<HttpRequestMessage> create, int status)
    {
        try { using var request = create(); Check(false, "The official endpoint contract must reject before dispatch."); }
        // 只捕获预期协议错误；意外异常不可被当作端点拒绝通过。
        catch (RelayFailure error) { Check(error.Status == status && error.RetryAfter == 0); }
    }
}
