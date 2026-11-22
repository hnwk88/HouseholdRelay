using System.Text;
using Core.Siyu;
using Infrastructure.ExternalSources.Siyu;
using static Tests.TestSuite;

namespace Tests;

/// <summary>思域内层 HTTP/1.1 定界合同与官方目标、路由白名单的离线样本：合同表每一行至少一个正例或反例。</summary>
public static class SiyuHttp1Tests
{
    /// <summary>CONNECT 目标只认两个精确官方 authority；内层 SNI、Host 必须是同一个主机。</summary>
    public static Task TargetsAreExact()
    {
        Check(OfficialTargets.TryParseConnectTarget("api.anthropic.com:443", out var api) && api == OfficialHost.Api, "api 目标应被接受");
        Check(OfficialTargets.TryParseConnectTarget("mcp-proxy.anthropic.com:443", out var mcp) && mcp == OfficialHost.McpProxy, "mcp-proxy 目标应被接受");
        foreach (var bad in new[] { "api.anthropic.com", "api.anthropic.com:8443", "API.anthropic.com:443", "api.anthropic.com.:443", "x.api.anthropic.com:443",
                     "user@api.anthropic.com:443", "160.79.104.10:443", "api%2Eanthropic.com:443", "claude.ai:443", "platform.claude.com:443", " api.anthropic.com:443" })
            Check(!OfficialTargets.TryParseConnectTarget(bad, out _), "不应接受目标 " + bad);
        Check(OfficialTargets.MatchesInnerName(OfficialHost.Api, "api.anthropic.com", false), "SNI 应匹配");
        Check(!OfficialTargets.MatchesInnerName(OfficialHost.Api, "mcp-proxy.anthropic.com", false), "隧道主机与 SNI 不同应拒绝");
        Check(OfficialTargets.MatchesInnerName(OfficialHost.Api, "api.anthropic.com:443", true) && !OfficialTargets.MatchesInnerName(OfficialHost.Api, "api.anthropic.com:443", false), "Host 可带 :443，SNI 不可");
        return Task.CompletedTask;
    }

    /// <summary>路由按主机、方法、精确路径放行；连接器编号只取字符集合法的一段，查询串按用途限定。</summary>
    public static Task RoutesAreExact()
    {
        void Accept(OfficialHost host, string method, string target, SiyuPurpose purpose, string? id = null)
        {
            Check(SiyuRoutes.TryMatch(host, method, target, out var route) && route.Purpose == purpose && route.ConnectorId == id, $"应放行 {host} {method} {target}");
        }
        void Refuse(OfficialHost host, string method, string target) => Check(!SiyuRoutes.TryMatch(host, method, target, out _), $"应拒绝 {host} {method} {target}");
        Accept(OfficialHost.Api, "POST", "/v1/messages?beta=true", SiyuPurpose.Messages);
        Accept(OfficialHost.Api, "POST", "/v1/messages", SiyuPurpose.Messages);
        Accept(OfficialHost.Api, "POST", "/v1/messages/count_tokens?beta=true", SiyuPurpose.CountTokens);
        Accept(OfficialHost.Api, "GET", "/v1/models?limit=1000", SiyuPurpose.Models);
        Accept(OfficialHost.Api, "GET", "/api/claude_code/settings", SiyuPurpose.Settings);
        Accept(OfficialHost.Api, "GET", "/api/claude_code/policy_limits", SiyuPurpose.PolicyLimits);
        Accept(OfficialHost.Api, "GET", "/v1/mcp_servers?limit=1000", SiyuPurpose.McpServers);
        Accept(OfficialHost.McpProxy, "POST", "/v1/mcp/mcpsrv_014Fj7srWvLMyCuRCS8J9W3d", SiyuPurpose.McpConnector, "mcpsrv_014Fj7srWvLMyCuRCS8J9W3d");
        Accept(OfficialHost.McpProxy, "GET", "/v1/mcp/mcpsrv_1", SiyuPurpose.McpConnector, "mcpsrv_1");
        Accept(OfficialHost.McpProxy, "DELETE", "/v1/mcp/mcpsrv_1", SiyuPurpose.McpConnector, "mcpsrv_1");
        Refuse(OfficialHost.Api, "GET", "/v1/messages");
        Refuse(OfficialHost.Api, "POST", "/v1/messages?beta=false");
        Refuse(OfficialHost.Api, "POST", "/v1/Messages");
        Refuse(OfficialHost.Api, "POST", "/v1/messages/");
        Refuse(OfficialHost.Api, "POST", "/v1//messages");
        Refuse(OfficialHost.Api, "POST", "/v1/./messages");
        Refuse(OfficialHost.Api, "POST", "/v1/%6dessages");
        Refuse(OfficialHost.Api, "POST", "https://api.anthropic.com/v1/messages");
        Refuse(OfficialHost.Api, "GET", "/api/oauth/profile");
        Refuse(OfficialHost.Api, "GET", "/api/event_logging/v2/batch");
        Refuse(OfficialHost.Api, "GET", "/v1/mcp_servers?x=<a>");
        Refuse(OfficialHost.Api, "POST", "/v1/mcp/mcpsrv_1");
        Refuse(OfficialHost.McpProxy, "POST", "/v1/messages");
        Refuse(OfficialHost.McpProxy, "POST", "/v1/mcp/");
        Refuse(OfficialHost.McpProxy, "POST", "/v1/mcp/a/b");
        Refuse(OfficialHost.McpProxy, "POST", "/v1/mcp/mcpsrv_1?x=1");
        Refuse(OfficialHost.McpProxy, "PUT", "/v1/mcp/mcpsrv_1");
        Refuse(OfficialHost.McpProxy, "POST", "/v1/mcp/" + new string('a', SiyuRoutes.MaxConnectorIdLength + 1));
        return Task.CompletedTask;
    }

    /// <summary>请求合同表：每一种歧义或不支持的写法都按固定状态码与原因码拒绝；合法请求在逐字节到达时也能读出同样的头。</summary>
    public static async Task RequestContract()
    {
        const string Ok = "POST /v1/messages?beta=true HTTP/1.1\r\nHost: api.anthropic.com\r\nAuthorization: Bearer hr_personal\r\nContent-Length: 2\r\n\r\n";
        var head = await ReadRequest(Ok + "{}", 1);
        Check(head.Method == "POST" && head.Target == "/v1/messages?beta=true" && head.ContentLength == 2 && !head.Close, "合法请求应解析");
        Check(head.Raw.SequenceEqual(Encoding.ASCII.GetBytes(Ok)), "原始头字节应原样保留");
        await Rejects("GET /v1/models HTTP/1.0\r\nHost: a\r\n\r\n", 505, "version");
        await Rejects("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n", 505, "version");
        await Rejects("GET  /v1/models HTTP/1.1\r\n\r\n", 400, "request-line");
        await Rejects("G(T /v1/models HTTP/1.1\r\n\r\n", 400, "request-line");
        await Rejects("GET /v1/models HTTP/1.1\r\nHost : a\r\n\r\n", 400, "header-name");
        await Rejects("GET /v1/models HTTP/1.1\r\nHo st: a\r\n\r\n", 400, "header-name");
        await Rejects("GET /v1/models HTTP/1.1\r\nX-A: b\u0001c\r\n\r\n", 400, "header-value");
        await Rejects("GET /v1/models HTTP/1.1\r\nX-A: b\u007f\r\n\r\n", 400, "header-value");
        await RejectsBytes([.. Encoding.ASCII.GetBytes("GET /v1/models HTTP/1.1\r\nX-A: "), 0xC3, 0xA9, .. Encoding.ASCII.GetBytes("\r\n\r\n")], 400, "header-value");
        await Rejects("GET /v1/models HTTP/1.1\r\nX-A: b\r\n c\r\n\r\n", 400, "obs-fold");
        await Rejects("GET /v1/models HTTP/1.1\nHost: a\r\n\r\n", 400, "bare-line-ending");
        await Rejects("GET /v1/models HTTP/1.1\r\nHost: a\rX: b\r\n\r\n", 400, "bare-line-ending");
        await Rejects("GET /v1/models HTTP/1.1\r\n" + string.Concat(Enumerable.Repeat("X-Pad: " + new string('a', 200) + "\r\n", 200)) + "\r\n", 431, "head-too-large");
        await Rejects("GET /v1/models HTTP/1.1\r\n" + string.Concat(Enumerable.Range(0, 130).Select(i => $"X-{i}: a\r\n")) + "\r\n", 431, "head-too-large");
        await Rejects("POST /v1/messages HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n\r\n", 501, "transfer-encoding");
        await Rejects("POST /v1/messages HTTP/1.1\r\nContent-Length: 2\r\nTransfer-Encoding: chunked\r\n\r\n", 501, "transfer-encoding");
        await Rejects("POST /v1/messages HTTP/1.1\r\nContent-Length: 2\r\nContent-Length: 2\r\n\r\n{}", 400, "content-length");
        await Rejects("POST /v1/messages HTTP/1.1\r\nContent-Length: 2, 2\r\n\r\n{}", 400, "content-length");
        await Rejects("POST /v1/messages HTTP/1.1\r\nContent-Length: +2\r\n\r\n{}", 400, "content-length");
        await Rejects("POST /v1/messages HTTP/1.1\r\nContent-Length: 0x2\r\n\r\n{}", 400, "content-length");
        await Rejects("POST /v1/messages HTTP/1.1\r\nHost: a\r\n\r\n", 411, "length-required");
        await Rejects("POST /v1/messages HTTP/1.1\r\nContent-Length: 11\r\n\r\n", 413, "body-too-large");
        await Rejects("POST /v1/messages HTTP/1.1\r\nContent-Length: 2\r\nExpect: 100-continue\r\n\r\n", 417, "expect");
        await Rejects("GET /v1/models HTTP/1.1\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n\r\n", 400, "upgrade");
        await Rejects("GET /v1/models HTTP/1.1\r\nConnection: keep-alive, upgrade\r\n\r\n", 400, "upgrade");
        var closing = await ReadRequest("GET /v1/models HTTP/1.1\r\nConnection: keep-alive, Close\r\n\r\n", 7);
        Check(closing.Close && closing.ContentLength == 0, "Connection: close 应被识别，GET 无体为 0");
    }

    /// <summary>流水线：两个请求的字节一次到达，按顺序各读出自己的头和体，互不串。半截头在连接关闭时报中途断开，干净关闭返回 null。</summary>
    public static async Task PipelinedRequestsStayInOrder()
    {
        var bytes = Encoding.ASCII.GetBytes("POST /v1/messages HTTP/1.1\r\nContent-Length: 3\r\n\r\nabcGET /v1/models HTTP/1.1\r\nHost: a\r\n\r\n");
        foreach (var step in new[] { 1, 5, bytes.Length })
        {
            var reader = new Http1Reader(new TrickleStream(bytes, step));
            var first = Http1RequestHead.Parse((await reader.ReadHeadAsync(CancellationToken.None))!, 10);
            var body = await reader.ReadExactAsync((int)first.ContentLength, CancellationToken.None);
            var second = Http1RequestHead.Parse((await reader.ReadHeadAsync(CancellationToken.None))!, 10);
            Check(first.Method == "POST" && Encoding.ASCII.GetString(body) == "abc" && second.Method == "GET" && second.Target == "/v1/models", $"步长 {step} 流水线应按顺序读出");
            Check(await reader.ReadHeadAsync(CancellationToken.None) is null, "报文边界上的干净关闭应返回 null");
        }
        var truncated = new Http1Reader(new MemoryStream(Encoding.ASCII.GetBytes("GET /v1/models HTTP/1.1\r\nHost:")));
        var threw = false;
        try { await truncated.ReadHeadAsync(CancellationToken.None); }
        // 只接住合同规定的那一种异常来判定结果；别的异常照常抛出，让用例失败。
        catch (EndOfStreamException) { threw = true; }
        Check(threw, "半截头段应报中途断开");
    }

    /// <summary>授权替换只动唯一 Authorization 的值：其他头的名字、大小写、顺序、重复项、空白都逐字节不变；多个认证来源一律拒绝。</summary>
    public static Task AuthorizationSwapTouchesOneValue()
    {
        const string Before = "POST /v1/messages?beta=true HTTP/1.1\r\nhost: api.anthropic.com\r\nX-Dup: 1\r\nauthorization:   Bearer hr_personal_token  \r\nX-Dup: 2\r\nanthropic-beta: a,b\r\nContent-Length: 0\r\n\r\n";
        var head = Http1RequestHead.Parse(Encoding.ASCII.GetBytes(Before), 10);
        Check(head.BearerToken() == "hr_personal_token", "应取出个人令牌");
        var after = Encoding.ASCII.GetString(head.WithAuthorization("sk-ant-oat01-official"));
        Check(after == Before.Replace("Bearer hr_personal_token", "Bearer sk-ant-oat01-official", StringComparison.Ordinal), "只换 Authorization 的值，周围空白与其他头逐字节不变");
        foreach (var bad in new[]
        {
            "GET /v1/models HTTP/1.1\r\nHost: a\r\n\r\n",
            "GET /v1/models HTTP/1.1\r\nAuthorization: Bearer a\r\nAuthorization: Bearer b\r\n\r\n",
            "GET /v1/models HTTP/1.1\r\nAuthorization: Bearer a\r\nx-api-key: k\r\n\r\n",
            "GET /v1/models HTTP/1.1\r\nAuthorization: Bearer a\r\nProxy-Authorization: Basic eA==\r\n\r\n",
            "GET /v1/models HTTP/1.1\r\nAuthorization: Basic eDp5\r\n\r\n",
            "GET /v1/models HTTP/1.1\r\nAuthorization: Bearer \r\n\r\n",
            "GET /v1/models HTTP/1.1\r\nAuthorization: Bearer a b\r\n\r\n",
        })
        {
            var rejected = false;
            try { Http1RequestHead.Parse(Encoding.ASCII.GetBytes(bad), 10).BearerToken(); }
            // 只接住合同规定的那一种异常来判定结果；别的异常照常抛出，让用例失败。
            catch (Http1Rejection error) { rejected = error.Status == 400 && error.Code == "authorization"; }
            Check(rejected, "认证来源不唯一或格式不对应拒绝：" + bad.Split("\r\n")[1]);
        }
        return Task.CompletedTask;
    }

    /// <summary>回应合同表：定长、chunked（含块扩展与 trailer）、读到关闭、无体、中间回应各自原样转发；走私信号、升级、坏块长一律拒绝。</summary>
    public static async Task ResponseContract()
    {
        Check(Response("HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\n", "GET") is { Framing: Http1BodyFraming.Length, ContentLength: 5, Close: false }, "定长回应");
        Check(Response("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n", "POST") is { Framing: Http1BodyFraming.Chunked }, "chunked 回应");
        Check(Response("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\n\r\n", "POST") is { Framing: Http1BodyFraming.UntilClose, Close: true }, "既无长度也非 chunked 读到关闭");
        Check(Response("HTTP/1.1 204 No Content\r\n\r\n", "DELETE") is { Framing: Http1BodyFraming.None }, "204 无体");
        Check(Response("HTTP/1.1 304 Not Modified\r\nContent-Length: 9\r\n\r\n", "GET") is { Framing: Http1BodyFraming.None }, "304 无体");
        Check(Response("HTTP/1.1 200 OK\r\nContent-Length: 9\r\n\r\n", "HEAD") is { Framing: Http1BodyFraming.None }, "HEAD 无体");
        Check(Response("HTTP/1.1 100 Continue\r\n\r\n", "POST") is { Interim: true, Framing: Http1BodyFraming.None }, "100 是中间回应");
        Check(Response("HTTP/1.1 200\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", "GET") is { Framing: Http1BodyFraming.None, Close: true }, "无原因短语也可，Connection: close 要关");
        ResponseRejects("HTTP/1.1 200 OK\r\nContent-Length: 5\r\nTransfer-Encoding: chunked\r\n\r\n", "transfer-encoding-and-length");
        ResponseRejects("HTTP/1.1 200 OK\r\nTransfer-Encoding: gzip, chunked\r\n\r\n", "transfer-encoding");
        ResponseRejects("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nTransfer-Encoding: chunked\r\n\r\n", "transfer-encoding");
        ResponseRejects("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\n\r\n", "upgrade");
        ResponseRejects("HTTP/1.0 200 OK\r\n\r\n", "status-line");
        ResponseRejects("HTTP/1.1 2000 OK\r\n\r\n", "status-line");
        ResponseRejects("HTTP/1.1 200 OK\r\nContent-Length: 1\r\nContent-Length: 1\r\n\r\n", "content-length");

        const string Chunked = "4;name=\"v\"\r\ndata\r\nA\r\n0123456789\r\n0\r\nX-Trailer: done\r\n\r\n";
        foreach (var step in new[] { 1, 3, 4096 })
        {
            var output = new MemoryStream();
            var reader = new Http1Reader(new TrickleStream(Encoding.ASCII.GetBytes(Chunked + "HTTP/1.1 200 OK\r\n"), step));
            await reader.CopyChunkedAsync(output, CancellationToken.None);
            Check(Encoding.ASCII.GetString(output.ToArray()) == Chunked, $"步长 {step} chunked 应逐字节原样转发到 trailer 结束");
            Check(reader.HasBufferedBytes || step < 4096, "chunked 之后的字节应留给下一个回应");
        }
        foreach (var (bad, code) in new[]
        {
            ("G\r\nxx\r\n", "chunk-size"), ("\r\n", "chunk-size"), ("4 x\r\ndata\r\n0\r\n\r\n", "chunk-size"), ("4 \r\ndata\r\n0\r\n\r\n", "chunk-size"),
            ("11111111111111111\r\n", "chunk-size"), ("4\r\ndataXX0\r\n\r\n", "chunk-terminator"), ("0\r\nBad Trailer\r\n\r\n", "header-name"), ("4\ndata\r\n", "bare-line-ending"),
        })
        {
            var rejected = false;
            try { await new Http1Reader(new MemoryStream(Encoding.ASCII.GetBytes(bad))).CopyChunkedAsync(Stream.Null, CancellationToken.None); }
            // 只接住合同规定的那一种异常来判定结果；别的异常照常抛出，让用例失败。
            catch (Http1Rejection error) { rejected = error.Code == code; }
            Check(rejected, $"坏 chunked 应拒绝为 {code}：{bad.Replace("\r\n", "\\r\\n", StringComparison.Ordinal)}");
        }
        var cut = false;
        try { await new Http1Reader(new MemoryStream(Encoding.ASCII.GetBytes("A\r\n0123"))).CopyChunkedAsync(Stream.Null, CancellationToken.None); }
        // 只接住合同规定的那一种异常来判定结果；别的异常照常抛出，让用例失败。
        catch (EndOfStreamException) { cut = true; }
        Check(cut, "块没读完连接断开应报中途断开");

        var rest = new MemoryStream();
        var tail = new Http1Reader(new TrickleStream(Encoding.ASCII.GetBytes("data: a\n\ndata: b\n\n"), 2));
        await tail.CopyToEndAsync(rest, CancellationToken.None);
        Check(Encoding.ASCII.GetString(rest.ToArray()) == "data: a\n\ndata: b\n\n", "读到关闭应原样转发全部字节");
    }

    /// <summary>逐字节（或按步长）喂给读取器，读出一个请求头并按 10 字节上限解析。</summary>
    private static async Task<Http1RequestHead> ReadRequest(string text, int step)
    {
        var reader = new Http1Reader(new TrickleStream(Encoding.ASCII.GetBytes(text), step));
        return Http1RequestHead.Parse((await reader.ReadHeadAsync(CancellationToken.None))!, 10);
    }

    /// <summary>断言这段请求被拒绝为指定状态码与原因码。</summary>
    private static Task Rejects(string text, int status, string code) => RejectsBytes(Encoding.ASCII.GetBytes(text), status, code);

    /// <summary>断言这段请求字节被拒绝为指定状态码与原因码（读头或解析阶段都算）。</summary>
    private static async Task RejectsBytes(byte[] bytes, int status, string code)
    {
        Http1Rejection? error = null;
        try { Http1RequestHead.Parse((await new Http1Reader(new TrickleStream(bytes, 3)).ReadHeadAsync(CancellationToken.None))!, 10); }
        // 只接住合同规定的那一种异常来判定结果；别的异常照常抛出，让用例失败。
        catch (Http1Rejection rejection) { error = rejection; }
        var line = Encoding.ASCII.GetString(bytes).Split('\n')[0].TrimEnd('\r');
        Check(error is not null && error.Status == status && error.Code == code, $"应拒绝为 {status} {code}，实际 {error?.Status} {error?.Code}：{line}");
    }

    /// <summary>解析一段回应头。</summary>
    private static Http1ResponseHead Response(string text, string method) => Http1ResponseHead.Parse(Encoding.ASCII.GetBytes(text), method);

    /// <summary>断言这段回应头被拒绝为指定原因码。</summary>
    private static void ResponseRejects(string text, string code)
    {
        string? actual = null;
        try { Response(text, "GET"); }
        // 只接住合同规定的那一种异常来判定结果；别的异常照常抛出，让用例失败。
        catch (Http1Rejection error) { actual = error.Code; }
        Check(actual == code, $"回应应拒绝为 {code}，实际 {actual}：{text.Split("\r\n")[0]}");
    }
}
