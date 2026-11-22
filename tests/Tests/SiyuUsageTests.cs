using System.IO.Compression;
using System.Text;
using Core.Siyu;
using Core.Usage;
using Infrastructure.ExternalSources.Siyu;
using static Tests.TestSuite;

namespace Tests;

/// <summary>思域用量旁路：事件流与 JSON 回应的 token 计数、分块与压缩、中途断开；代理端到端时转发字节不变、已发出与结束都报告。</summary>
public static class SiyuUsageTests
{
    private const string Sse =
        "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":11,\"cache_read_input_tokens\":3}}}\n\n"
        + "event: content_block_delta\r\ndata: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"hi\"}}\r\n\r\n"
        + "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":7}}\n\n"
        + "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";

    /// <summary>旁路解析：chunked 事件流按 1、3、50 字节到达都算出同样的计数且协议完整；缺 message_stop 时不完整；gzip 的 JSON 读出计数；中途断开的 JSON 不计。</summary>
    public static Task TapCountsTokens()
    {
        var head = Head("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nTransfer-Encoding: chunked\r\n\r\n");
        var wire = Chunked(Encoding.UTF8.GetBytes(Sse), 17);
        foreach (var step in new[] { 1, 3, 50 })
        {
            var usage = new UsageAccumulator(UsageKind.Inference);
            var tap = new SiyuUsageTap(usage, head);
            for (var offset = 0; offset < wire.Length; offset += step) tap.Observe(wire.AsSpan(offset, Math.Min(step, wire.Length - offset)));
            tap.Complete(true);
            Check(usage.IsStream && usage.ProtocolComplete && usage.Tokens == new UsageTokens(11, 7, 3, null), $"按 {step} 字节到达：计数 11/7/3、协议完整");
        }
        var gzSse = new MemoryStream(); using (var zip = new GZipStream(gzSse, CompressionLevel.Fastest, true)) zip.Write(Encoding.UTF8.GetBytes(Sse));
        var compressedStream = new UsageAccumulator(UsageKind.Inference);
        var compressedTap = new SiyuUsageTap(compressedStream, Head("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nContent-Encoding: gzip\r\nTransfer-Encoding: chunked\r\n\r\n"));
        var compressedWire = Chunked(gzSse.ToArray(), 13);
        for (var offset = 0; offset < compressedWire.Length; offset += 7) compressedTap.Observe(compressedWire.AsSpan(offset, Math.Min(7, compressedWire.Length - offset)));
        compressedTap.Complete(true);
        Check(compressedStream.ProtocolComplete && compressedStream.Tokens == new UsageTokens(11, 7, 3, null), "gzip 压缩的事件流结束后解码，计数 11/7/3、协议完整");
        var zstd = new UsageAccumulator(UsageKind.Inference);
        var zstdTap = new SiyuUsageTap(zstd, Head("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nContent-Encoding: zstd\r\nContent-Length: 4\r\n\r\n"));
        zstdTap.Observe("abcd"u8); zstdTap.Complete(true);
        Check(zstd.ErrorType == "unreadable" && !zstd.ProtocolComplete, "zstd 解不开：记看不懂");
        var cut = new UsageAccumulator(UsageKind.Inference);
        var cutTap = new SiyuUsageTap(cut, Head("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nContent-Length: 10\r\n\r\n"));
        var partial = Encoding.UTF8.GetBytes(Sse[..Sse.IndexOf("event: message_stop", StringComparison.Ordinal)]);
        cutTap.Observe(partial); cutTap.Complete(false);
        Check(cut.IsStream && !cut.ProtocolComplete && cut.Tokens.Input == 11, "缺 message_stop：有计数但协议不完整");

        var json = "{\"type\":\"message\",\"usage\":{\"input_tokens\":5,\"output_tokens\":2}}"u8.ToArray();
        var gz = new MemoryStream(); using (var zip = new GZipStream(gz, CompressionLevel.Fastest, true)) zip.Write(json);
        var compressed = gz.ToArray();
        var jsonUsage = new UsageAccumulator(UsageKind.Inference);
        var jsonTap = new SiyuUsageTap(jsonUsage, Head($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Encoding: gzip\r\nContent-Length: {compressed.Length}\r\n\r\n"));
        jsonTap.Observe(compressed); jsonTap.Complete(true);
        Check(!jsonUsage.IsStream && jsonUsage.ProtocolComplete && jsonUsage.Tokens.Input == 5 && jsonUsage.Tokens.Output == 2, "gzip 的 JSON 解码后读出计数");
        var broken = new UsageAccumulator(UsageKind.Inference);
        var brokenTap = new SiyuUsageTap(broken, Head("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 99\r\n\r\n"));
        brokenTap.Observe(json.AsSpan(0, 10)); brokenTap.Complete(false);
        Check(!broken.ProtocolComplete && broken.Tokens.Input is null, "中途断开的 JSON 不计");
        var error = new UsageAccumulator(UsageKind.Inference);
        var errorTap = new SiyuUsageTap(error, Head("HTTP/1.1 529 Overloaded\r\nContent-Type: application/json\r\nContent-Length: 70\r\n\r\n"));
        errorTap.Observe("{\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"busy\"}}"u8); errorTap.Complete(true);
        Check(error.Error && error.ErrorType == "overloaded_error", "错误回应读出错误类型");
        return Task.CompletedTask;
    }

    /// <summary>代理端到端：推理请求的事件流原样到客户端；许可拿到原请求体作已发出、累加器算出计数、结束报告 200 且完整；非用量用途没有累加器。</summary>
    public static async Task ProxyFeedsUsageWithoutChangingBytes()
    {
        const string body = "{\"model\":\"claude-haiku-5-5\",\"max_tokens\":8,\"messages\":[]}";
        var reply = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nTransfer-Encoding: chunked\r\n\r\n").Concat(Chunked(Encoding.UTF8.GetBytes(Sse), 23)).ToArray();
        var settings = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 2\r\n\r\n{}"u8.ToArray();
        await using var rig = await SiyuProxyRig.Start();
        rig.Connector.Replies = _ => [.. reply, .. settings];
        var request = $"POST /v1/messages?beta=true HTTP/1.1\r\nHost: api.anthropic.com\r\nAuthorization: Bearer {FakeSiyuTunnel.PersonalToken}\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\n\r\n{body}"
            + $"GET /api/claude_code/settings HTTP/1.1\r\nHost: api.anthropic.com\r\nAuthorization: Bearer {FakeSiyuTunnel.PersonalToken}\r\n\r\n";
        var (tcp, inner) = await rig.OpenInner(OfficialHost.Api);
        using (tcp)
        await using (inner)
        {
            await inner.WriteAsync(Encoding.ASCII.GetBytes(request));
            await inner.FlushAsync();
            var reader = new Http1Reader(inner);
            var received = new MemoryStream();
            await SiyuProxyRig.ReadResponse(reader, "POST", received);
            await SiyuProxyRig.ReadResponse(reader, "GET", received);
            Check(received.ToArray().SequenceEqual([.. reply, .. settings]), "客户端逐字节收到原回应");
        }
        var permits = rig.Gate.Tunnels.Single().Permits;
        var messages = permits[0];
        Check(messages.DispatchedBody is { } sent && Encoding.UTF8.GetString(sent) == body, "已发出时交来的是原请求体");
        Check(messages.Usage is { ProtocolComplete: true } usage && usage.Tokens == new UsageTokens(11, 7, 3, null) && messages.Completion == (200, true), "累加器算出计数，结束报告 200 且完整");
        Check(permits[1].Usage is null && permits[1].Completion == (200, true), "远程设置不记用量，但同样报告结束");
    }

    /// <summary>解析一个回应头。</summary>
    private static Http1ResponseHead Head(string raw) => Http1ResponseHead.Parse(Encoding.ASCII.GetBytes(raw), "POST");

    /// <summary>按 <paramref name="size"/> 字节切成 chunked 线上字节。</summary>
    private static byte[] Chunked(byte[] data, int size)
    {
        var output = new MemoryStream();
        for (var offset = 0; offset < data.Length; offset += size)
        {
            var length = Math.Min(size, data.Length - offset);
            output.Write(Encoding.ASCII.GetBytes(length.ToString("x", System.Globalization.CultureInfo.InvariantCulture) + ";ext=1\r\n"));
            output.Write(data, offset, length);
            output.Write("\r\n"u8);
        }
        output.Write("0\r\nX-Trailer: 1\r\n\r\n"u8);
        return output.ToArray();
    }
}
