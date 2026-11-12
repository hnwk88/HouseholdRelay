using System.Collections.Concurrent;
using System.Text.Json;
using Application.Services.Relay;
using static Tests.TestSuite;

namespace Tests;

/// <summary>临时取证预算与秘密白名单；接口不接收正文，关闭时没有正文扫描或复制入口。</summary>
static class ProtocolDiagnosticsTests
{
    /// <summary>默认关闭、十分钟和一百样本任一到达即停止，重建实例不继承开启状态。</summary>
    public static async Task BudgetsAndSecrecy()
    {
        var logs = new ConcurrentQueue<string>();
        var clock = new UsageClock(DateTimeOffset.UtcNow);
        var trace = new ProtocolDiagnostics(new FixtureLog(logs), clock);
        const string secret = "fixture-private-token-cookie-account-directory-content";
        for (var i = 0; i < 1000; i++) trace.Record(secret, secret, secret, 1, secret, secret, secret, 403, secret);
        Check(logs.IsEmpty, "Disabled diagnostics cannot log or scan a body: no body is accepted by this API");
        trace.Start();
        // 同时竞争最后一个样本预算，验证限制对并发调用同样生效。
        await Task.WhenAll(Enumerable.Range(0, 160).Select(_ => Task.Run(() => trace.Record(secret, secret, secret, 1, secret, secret, secret, 403, secret))));
        Check(logs.Count == 100 && trace.Status.Contains("关闭", StringComparison.Ordinal), "Sample limit must be atomic");
        Check(logs.All(line => !line.Contains(secret, StringComparison.Ordinal)), "Only hashes or approved fixed categories may be logged");
        trace.Start(); clock.Advance(TimeSpan.FromMinutes(10));
        trace.Record("request", "dispatch", "/v1/messages", 1, new string('a', 64));
        Check(logs.Count == 100, "Exact ten-minute boundary stops collection");
        trace.Start(); trace.Record("request", "assembled", "/v1/messages", 2, new string('a', 64), "2.1.319", "cli");
        trace.Record("request", "dispatch", "/v1/messages", 2, new string('a', 64));
        trace.Record("request", "response", "/v1/messages", 2, new string('a', 64), status: 403, error: "verification");
        var last = logs.TakeLast(3).Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            Check(last.Select(document => document.RootElement.GetProperty("stage").GetString()).SequenceEqual(["assembled", "dispatch", "response"]));
            Check(last.All(document => document.RootElement.GetProperty("environment").GetString() == "unknown"));
        }
        // JSON 证据只在测试内存中检查，断言失败也归还解析器内存。
        finally { foreach (var document in last) document.Dispose(); }
        var restarted = new ProtocolDiagnostics(new FixtureLog(logs), clock);
        restarted.Record("request", "dispatch", "/v1/messages", 2, new string('a', 64));
        Check(logs.Count == 103 && restarted.Status.Contains("关闭", StringComparison.Ordinal));
        trace.Stop(); trace.Record("request", "dispatch", "/v1/messages", 2, new string('a', 64));
        Check(logs.Count == 103);
    }
}
