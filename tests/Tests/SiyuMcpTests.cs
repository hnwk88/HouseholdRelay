using System.IO.Compression;
using System.Text;
using Core.Siyu;
using Infrastructure.ExternalSources.Siyu;
using static Tests.TestSuite;

namespace Tests;

/// <summary>思域连接器发现：清单解析、回应实体解码、代理旁路学到编号且回应原样到客户端、补发现按模板重发。</summary>
public static class SiyuMcpTests
{
    private const string List = """{"data":[{"type":"mcp_server","id":"mcpsrv_A1","display_name":"Gmail","url":"https://gmail.example/mcp","created_at":"2026-01-01T00:00:00Z"},{"type":"mcp_server","id":"mcpsrv_b-2","display_name":"x","url":"https://x.example","created_at":"2026-01-01T00:00:00Z"}],"has_more":false,"next_page":null}""";

    /// <summary>清单只认 data[].id，形状或编号不合整份不认。</summary>
    public static Task ServerListReadsIdsOnly()
    {
        Check(McpServerList.TryReadIds(Encoding.UTF8.GetBytes(List), out var ids) && ids.SequenceEqual(["mcpsrv_A1", "mcpsrv_b-2"]), "两个编号按顺序读出");
        Check(McpServerList.TryReadIds("""{"data":[],"has_more":false}"""u8, out var none) && none.Count == 0, "空清单是合法的空集合");
        foreach (var (label, json) in new[]
        {
            ("不是 JSON", "nope"), ("顶层是数组", "[]"), ("没有 data", """{"items":[]}"""), ("data 不是数组", """{"data":{}}"""),
            ("项不是对象", """{"data":["mcpsrv_1"]}"""), ("缺 id", """{"data":[{"type":"mcp_server"}]}"""), ("id 不是字符串", """{"data":[{"id":7}]}"""),
            ("id 带斜杠", """{"data":[{"id":"a/b"}]}"""), ("id 为空", """{"data":[{"id":""}]}"""), ("id 太长", "{\"data\":[{\"id\":\"" + new string('a', 129) + "\"}]}"),
            ("项太多", "{\"data\":[" + string.Join(',', Enumerable.Repeat("{\"id\":\"a\"}", McpServerList.MaxConnectors + 1)) + "]}"),
        })
            Check(!McpServerList.TryReadIds(Encoding.UTF8.GetBytes(json), out _), "应整份不认：" + label);
        return Task.CompletedTask;
    }

    /// <summary>实体解码：chunked、四种编码、未知或多个编码、损坏、超限。</summary>
    public static Task EntityDecodesForInspectionOnly()
    {
        var plain = Encoding.UTF8.GetBytes(List);
        var fields = (string? encoding) => encoding is null ? new List<Http1Header>() : [new Http1Header("Content-Encoding", encoding, 0, 0)];
        Check(Http1Entity.Decode(plain, Http1BodyFraming.Length, fields(null))!.SequenceEqual(plain), "无编码原样");
        Check(Http1Entity.Decode(Chunked(plain, 7), Http1BodyFraming.Chunked, fields("identity"))!.SequenceEqual(plain), "分块按 7 字节切、identity");
        Check(Http1Entity.Decode(Chunked(Gzip(plain), 5), Http1BodyFraming.Chunked, fields("gzip"))!.SequenceEqual(plain), "分块加 gzip");
        Check(Http1Entity.Decode(Compress(plain, s => new ZLibStream(s, CompressionLevel.Fastest, true)), Http1BodyFraming.Length, fields("deflate"))!.SequenceEqual(plain), "deflate");
        Check(Http1Entity.Decode(Compress(plain, s => new BrotliStream(s, CompressionLevel.Fastest, true)), Http1BodyFraming.Length, fields("BR"))!.SequenceEqual(plain), "br（大小写不敏感）");
        Check(Http1Entity.Decode(plain, Http1BodyFraming.Length, fields("zstd")) is null, "不认识的编码看不懂");
        Check(Http1Entity.Decode(plain, Http1BodyFraming.Length, fields("gzip, br")) is null, "多个编码看不懂");
        Check(Http1Entity.Decode(plain, Http1BodyFraming.Length, fields("gzip")) is null, "声称 gzip 却不是：看不懂");
        Check(Http1Entity.Decode(Gzip(new byte[Http1Entity.MaxDecodedBytes + 1]), Http1BodyFraming.Length, fields("gzip")) is null, "解压后超限看不懂");
        Check(Http1Entity.Decode("5\r\nabc"u8.ToArray(), Http1BodyFraming.Chunked, fields(null)) is null, "分块残缺看不懂");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 代理端到端：客户端经思域发 <c>GET /v1/mcp_servers</c>，官方回 chunked 加 gzip 的清单；客户端逐字节收到原回应，
    /// 编号在回应转出之前报告给会话层，模板里个人令牌已换成占位值；官方收到的只换了 Authorization。非 200 的发现回应不报告。
    /// </summary>
    public static async Task ProxyLearnsDiscoveryAndForwardsBytes()
    {
        var entity = Gzip(Encoding.UTF8.GetBytes(List));
        var ok = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Encoding: gzip\r\nTransfer-Encoding: chunked\r\n\r\n").Concat(Chunked(entity, 9)).ToArray();
        var denied = "HTTP/1.1 403 Forbidden\r\nContent-Type: application/json\r\nContent-Length: 2\r\n\r\n{}"u8.ToArray();
        await using var rig = await SiyuProxyRig.Start();
        rig.Connector.Replies = _ => [.. ok, .. denied];
        var request = $"GET /v1/mcp_servers?limit=1000 HTTP/1.1\r\nAccept: application/json, text/plain, */*\r\nAuthorization: Bearer {FakeSiyuTunnel.PersonalToken}\r\nanthropic-beta: mcp-servers-2025-12-04\r\nAccept-Encoding: gzip, br\r\nHost: api.anthropic.com\r\n\r\n";
        var (tcp, inner) = await rig.OpenInner(OfficialHost.Api);
        using (tcp)
        await using (inner)
        {
            await inner.WriteAsync(Encoding.ASCII.GetBytes(request + request));
            await inner.FlushAsync();
            var reader = new Http1Reader(inner);
            var received = new MemoryStream();
            await SiyuProxyRig.ReadResponse(reader, "GET", received);
            await SiyuProxyRig.ReadResponse(reader, "GET", received);
            Check(received.ToArray().SequenceEqual([.. ok, .. denied]), "客户端逐字节收到两个原回应");
        }
        var tunnel = rig.Gate.Tunnels.Single();
        Check(tunnel.Discoveries.Count == 1 && tunnel.Discoveries[0].Ids.SequenceEqual(["mcpsrv_A1", "mcpsrv_b-2"]), "只有 200 的那次报告了编号");
        var template = Encoding.ASCII.GetString(tunnel.Discoveries[0].Template);
        Check(!template.Contains(FakeSiyuTunnel.PersonalToken, StringComparison.Ordinal) && template == request.Replace(FakeSiyuTunnel.PersonalToken, SiyuInnerExchange.TemplatePlaceholder, StringComparison.Ordinal),
            "模板是原请求头，只有个人令牌换成占位值");
        Check(tunnel.Observed.SequenceEqual([200, 403]), "两次最终状态各报告一次");
        var sent = Encoding.ASCII.GetString(rig.Connector.Opened.Single().Stream.Written.ToArray());
        Check(sent == (request + request).Replace(FakeSiyuTunnel.PersonalToken, FakeSiyuTunnel.OfficialToken, StringComparison.Ordinal), "官方收到的只换了 Authorization");
    }

    /// <summary>补发现的出站实现：模板只换 Authorization 原样发出；200 读出编号；非 200、读不懂、模板不是发现请求都返回 null。</summary>
    public static async Task ProbeReplaysTemplate()
    {
        var template = Encoding.ASCII.GetBytes($"GET /v1/mcp_servers?limit=1000 HTTP/1.1\r\nAuthorization: Bearer {SiyuInnerExchange.TemplatePlaceholder}\r\nHost: api.anthropic.com\r\n\r\n");
        var connector = new FakeOfficialConnector();
        var probe = new McpDiscoveryProbe(connector);
        var body = Encoding.UTF8.GetBytes(List);
        connector.Replies = _ => [.. Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\n\r\n"), .. body];
        var ids = await probe.DiscoverAsync(template, "official-token", default);
        Check(ids is not null && ids.SequenceEqual(["mcpsrv_A1", "mcpsrv_b-2"]), "200 读出编号");
        Check(Encoding.ASCII.GetString(connector.Opened.Single().Stream.Written.ToArray()) == Encoding.ASCII.GetString(template).Replace(SiyuInnerExchange.TemplatePlaceholder, "official-token", StringComparison.Ordinal),
            "发出的就是模板，只换了 Authorization");
        connector.Replies = _ => "HTTP/1.1 401 Unauthorized\r\nContent-Length: 0\r\n\r\n"u8.ToArray();
        Check(await probe.DiscoverAsync(template, "official-token", default) is null, "非 200 补不到");
        connector.Replies = _ => "HTTP/1.1 200 OK\r\nContent-Length: 4\r\n\r\nnope"u8.ToArray();
        Check(await probe.DiscoverAsync(template, "official-token", default) is null, "读不懂补不到");
        connector.Replies = _ => [];
        Check(await probe.DiscoverAsync(template, "official-token", default) is null, "官方没回应补不到");
        var opened = connector.Opened.Count;
        Check(await probe.DiscoverAsync("GET /v1/models HTTP/1.1\r\nAuthorization: Bearer x\r\nHost: api.anthropic.com\r\n\r\n"u8.ToArray(), "official-token", default) is null
            && connector.Opened.Count == opened, "模板不是发现请求就不发");
    }

    /// <summary>按 <paramref name="size"/> 字节切成 chunked 线上字节（含末块与空 trailer）。</summary>
    private static byte[] Chunked(byte[] data, int size)
    {
        var output = new MemoryStream();
        for (var offset = 0; offset < data.Length; offset += size)
        {
            var length = Math.Min(size, data.Length - offset);
            output.Write(Encoding.ASCII.GetBytes(length.ToString("x", System.Globalization.CultureInfo.InvariantCulture) + "\r\n"));
            output.Write(data, offset, length);
            output.Write("\r\n"u8);
        }
        output.Write("0\r\n\r\n"u8);
        return output.ToArray();
    }

    /// <summary>gzip 压缩。</summary>
    private static byte[] Gzip(byte[] data) => Compress(data, s => new GZipStream(s, CompressionLevel.Fastest, true));

    /// <summary>用给定压缩流压缩。</summary>
    private static byte[] Compress(byte[] data, Func<Stream, Stream> open)
    {
        var output = new MemoryStream();
        using (var stream = open(output)) stream.Write(data);
        return output.ToArray();
    }
}
