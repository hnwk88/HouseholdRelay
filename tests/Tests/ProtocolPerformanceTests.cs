using System.Diagnostics;
using System.Text;
using Core.Relay;
using static Tests.TestSuite;

namespace Tests;

/// <summary>同一进程、同一正文和次数比较旧兼容改写与严格官方改写，报告实测而不设猜测阈值。</summary>
static class ProtocolPerformanceTests
{
    /// <summary>测量每次改写的耗时和线程分配；两条规则均只在内存处理，没有文件或网络操作。</summary>
    public static Task CompareNormalization()
    {
        var target = new string('f', 64);
        var body = Encoding.UTF8.GetBytes("{\"metadata\":{\"user_id\":\"user_" + new string('a', 64)
            + "_account_fixture_session_session\"},\"messages\":[{\"role\":\"user\",\"content\":\"" + new string('x', 8192) + "\"}]}");
        const int iterations = 2048;
        for (var i = 0; i < 100; i++) { DeviceMetadata.Normalize(body, target); DeviceMetadata.ForOfficial("/v1/messages", body, target); }
        var old = Measure(() => DeviceMetadata.Normalize(body, target), iterations);
        var strict = Measure(() => DeviceMetadata.ForOfficial("/v1/messages", body, target), iterations);
        Check(DeviceMetadata.Normalize(body, target).SequenceEqual(DeviceMetadata.ForOfficial("/v1/messages", body, target)));
        Console.WriteLine($"NORMALIZATION PERFORMANCE: bytes={body.Length}; iterations={iterations}; "
            + $"legacy_ms={old.Milliseconds:F3}; strict_ms={strict.Milliseconds:F3}; "
            + $"legacy_bytes_per_call={old.Bytes / iterations}; strict_bytes_per_call={strict.Bytes / iterations}; file_io=0; network_io=0. "
            + "Measured in this process after 100 warmups; strict validation scans the complete document to reject later duplicate keys.");
        return Task.CompletedTask;
    }

    /// <summary>在当前线程连续测同一函数，保留返回对象到循环末尾，防止无结果计算被优化掉。</summary>
    private static (double Milliseconds, long Bytes) Measure(Func<byte[]> rewrite, int count)
    {
        var allocated = GC.GetAllocatedBytesForCurrentThread(); var timer = Stopwatch.StartNew(); byte[]? last = null;
        for (var i = 0; i < count; i++) last = rewrite();
        timer.Stop(); GC.KeepAlive(last);
        return (timer.Elapsed.TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - allocated);
    }
}
