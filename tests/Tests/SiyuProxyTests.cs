using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Core.Siyu;
using Infrastructure.ExternalSources.Siyu;
using static Tests.TestSuite;

namespace Tests;

/// <summary>
/// 思域代理入口的回环测试：真起 Kestrel 独立监听（外层 TLS 用测试站点证书，外层之后交给 <see cref="SiyuProxyConnectionHandler"/>），
/// 客户端走真实的两层 TLS（内层只信思域代理 CA）。会话层与官方上游用替身，官方回应是事先写好的字节。
/// </summary>
public static class SiyuProxyTests
{
    private const string Messages = "{\"model\":\"claude-haiku-5-5\",\"metadata\":{\"user_id\":\"{\\\"device_id\\\":\\\"d\\\"}\"},\"system\":[{\"type\":\"text\",\"text\":\"x-anthropic-billing-header: cch=1a2b3;\"}]}";

    /// <summary>端到端：两个请求一次流水线发出，官方收到的是只换了 Authorization 的原始字节，客户端收到的是官方原始回应字节（chunked 与定长）。</summary>
    public static async Task EndToEndPreservesBytes()
    {
        var sse = "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nTransfer-Encoding: chunked\r\nrequest-id: req_1\r\n\r\n"
            + Chunk("event: message_start\ndata: {}\n\n") + "0\r\n\r\n";
        var models = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 11\r\n\r\n{\"data\":[]}";
        await using var rig = await SiyuProxyRig.Start();
        rig.Connector.Replies = _ => Encoding.ASCII.GetBytes(sse + models);
        var first = $"POST /v1/messages?beta=true HTTP/1.1\r\nhost: api.anthropic.com\r\nX-Stainless-Retry-Count: 0\r\nanthropic-beta: oauth-2025-04-20,claude-code-20250219\r\nAuthorization: Bearer {FakeSiyuTunnel.PersonalToken}\r\nX-Dup: 1\r\nX-Dup: 2\r\nContent-Length: {Encoding.UTF8.GetByteCount(Messages)}\r\n\r\n";
        var second = $"GET /v1/models HTTP/1.1\r\nHost: api.anthropic.com:443\r\nAuthorization: Bearer {FakeSiyuTunnel.PersonalToken}\r\n\r\n";
        var (tcp, inner) = await rig.OpenInner(OfficialHost.Api);
        using (tcp)
        await using (inner)
        {
            await inner.WriteAsync(Encoding.UTF8.GetBytes(first + Messages + second));
            await inner.FlushAsync();
            var reader = new Http1Reader(inner);
            var received = new MemoryStream();
            foreach (var method in new[] { "POST", "GET" }) await SiyuProxyRig.ReadResponse(reader, method, received);
            Check(Encoding.ASCII.GetString(received.ToArray()) == sse + models, "客户端应逐字节收到官方的两个回应");
        }
        Check(rig.Connector.Opened.Count == 1 && rig.Connector.Opened[0].Host == OfficialHost.Api, "两个请求应复用同一条出站连接");
        var sent = Encoding.UTF8.GetString(rig.Connector.Opened[0].Stream.Written.ToArray());
        var expected = (first + Messages + second).Replace("Bearer " + FakeSiyuTunnel.PersonalToken, "Bearer " + FakeSiyuTunnel.OfficialToken, StringComparison.Ordinal);
        Check(sent == expected, "官方收到的应是只换了 Authorization 的原始字节（头顺序、大小写、重复项、正文都不变）");
        var tunnel = rig.Gate.Tunnels.Single();
        Check(tunnel.Admitted.Select(route => route.Purpose).SequenceEqual([SiyuPurpose.Messages, SiyuPurpose.Models]), "两个请求各准入一次，用途正确");
        Check(tunnel.Observed.SequenceEqual([200, 200]), "两个最终状态码各报告一次");
    }

    /// <summary>按实际字节数写出一块 chunked 数据（块长十六进制）。</summary>
    private static string Chunk(string data) => Encoding.UTF8.GetByteCount(data).ToString("x", System.Globalization.CultureInfo.InvariantCulture) + "\r\n" + data + "\r\n";

    /// <summary>外层与内层的每一种拒绝：固定状态码与原因码；被拒的请求一个字节都没发给官方。</summary>
    public static async Task RefusalsNeverReachOfficial()
    {
        await using var rig = await SiyuProxyRig.Start();
        var redirect = "HTTP/1.1 307 Temporary Redirect\r\nLocation: https://evil.example/\r\nContent-Length: 0\r\n\r\n";
        rig.Connector.Replies = _ => Encoding.ASCII.GetBytes(redirect);

        async Task Outer(string request, int status, string code, bool challenge = false)
        {
            var (tcp, outer) = await rig.OpenOuter();
            using (tcp)
            await using (outer)
            {
                await outer.WriteAsync(Encoding.ASCII.GetBytes(request));
                var (head, body) = await SiyuProxyRig.ReadOne(new Http1Reader(outer), "CONNECT");
                Check(head.Status == status && body.Contains("\"code\":\"" + code + "\"", StringComparison.Ordinal), $"外层应拒绝为 {status} {code}，实际 {head.Status} {body}");
                Check(challenge == head.Fields.Any(field => field.Is("Proxy-Authenticate")), "只有 407 带 Proxy-Authenticate");
            }
        }
        var good = SiyuProxyRig.Basic(FakeSiyuGate.SessionId, FakeSiyuGate.Ticket);
        await Outer($"CONNECT api.anthropic.com:443 HTTP/1.1\r\nHost: api.anthropic.com:443\r\nProxy-Authorization: Basic {SiyuProxyRig.Basic(FakeSiyuGate.SessionId, "wrong")}\r\n\r\n", 407, "proxy-authentication", true);
        await Outer("CONNECT api.anthropic.com:443 HTTP/1.1\r\nHost: api.anthropic.com:443\r\n\r\n", 407, "proxy-authentication", true);
        await Outer($"CONNECT claude.ai:443 HTTP/1.1\r\nHost: claude.ai:443\r\nProxy-Authorization: Basic {good}\r\n\r\n", 403, "target");
        await Outer($"CONNECT api.anthropic.com:443 HTTP/1.1\r\nHost: localhost\r\nProxy-Authorization: Basic {good}\r\n\r\n", 400, "host");
        await Outer($"GET / HTTP/1.1\r\nHost: localhost\r\nProxy-Authorization: Basic {good}\r\n\r\n", 405, "method");

        async Task Inner(string request, int status, string code, Action<FakeSiyuTunnel>? prepare = null)
        {
            rig.Gate.Prepare = prepare ?? (_ => { });
            var (tcp, inner) = await rig.OpenInner(OfficialHost.Api);
            using (tcp)
            await using (inner)
            {
                await inner.WriteAsync(Encoding.ASCII.GetBytes(request));
                var (head, body) = await SiyuProxyRig.ReadOne(new Http1Reader(inner), "POST");
                Check(head.Status == status && body.Contains("\"code\":\"" + code + "\"", StringComparison.Ordinal), $"内层应拒绝为 {status} {code}，实际 {head.Status} {body}");
                Check(!head.Fields.Any(field => field.Is("Location")), "本地拒绝不带 Location");
            }
        }
        var auth = "Authorization: Bearer " + FakeSiyuTunnel.PersonalToken + "\r\n";
        var opened = rig.Connector.Opened.Count;
        await Inner("POST /v1/messages HTTP/1.1\r\nHost: api.anthropic.com\r\nAuthorization: Bearer wrong\r\nContent-Length: 2\r\n\r\n{}", 401, "personal-token");
        await Inner("GET /api/oauth/profile HTTP/1.1\r\nHost: api.anthropic.com\r\n" + auth + "\r\n", 403, "route");
        await Inner("GET /v1/models HTTP/1.1\r\nHost: mcp-proxy.anthropic.com\r\n" + auth + "\r\n", 400, "host");
        await Inner("POST /v1/messages HTTP/1.1\r\nHost: api.anthropic.com\r\n" + auth + "Transfer-Encoding: chunked\r\n\r\n0\r\n\r\n", 501, "transfer-encoding");
        await Inner("POST /v1/messages HTTP/1.1\r\nHost: api.anthropic.com\r\n" + auth + "Content-Length: 2\r\n\r\n{}", 503, "session-closing", tunnel => tunnel.RevokeAfterAdmit = true);
        Check(rig.Connector.Opened.Count == opened, "被本地拒绝的请求不应打开任何出站连接");
        await Inner("POST /v1/messages HTTP/1.1\r\nHost: api.anthropic.com\r\n" + auth + "Content-Length: 2\r\n\r\n{}", 502, "redirect-refused");
        Check(rig.Gate.Tunnels.Last().Observed.SequenceEqual([307]), "3xx 也报告给会话层");
    }

    /// <summary>内层握手：SNI 必须是隧道主机，不合就不处理任何请求；ALPN 服务端只会选 http/1.1，客户端只报 h2 时绝不会协商出 h2。</summary>
    public static async Task InnerHandshakeIsPinned()
    {
        await using var rig = await SiyuProxyRig.Start();
        var (tcp, outer) = await rig.OpenOuter();
        using (tcp)
        await using (outer)
        {
            await SiyuProxyRig.Connect(outer, OfficialHost.Api);
            var refused = false;
            try
            {
                await using var inner = new SslStream(outer, true, (_, _, _, _) => true);
                await inner.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = OfficialTargets.McpProxyHost, ApplicationProtocols = [SslApplicationProtocol.Http11] });
                await inner.WriteAsync(Encoding.ASCII.GetBytes("GET /v1/models HTTP/1.1\r\nHost: api.anthropic.com\r\nAuthorization: Bearer " + FakeSiyuTunnel.PersonalToken + "\r\n\r\n"));
                refused = await new Http1Reader(inner).ReadHeadAsync(CancellationToken.None) is null;
            }
            // 服务端在握手后直接断开：客户端读到 0 字节或断开异常都算拒绝；只接住断开类异常，别的照常抛。
            catch (Exception error) when (error is IOException or System.Security.Authentication.AuthenticationException) { refused = true; }
            Check(refused, "隧道主机是 api，内层 SNI 报 mcp-proxy 应被拒绝");
        }
        var (tcp2, outer2) = await rig.OpenOuter();
        using (tcp2)
        await using (outer2)
        {
            await SiyuProxyRig.Connect(outer2, OfficialHost.Api);
            var negotiatedH2 = false;
            try
            {
                await using var inner = new SslStream(outer2, true, (_, _, _, _) => true);
                await inner.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = OfficialTargets.ApiHost, ApplicationProtocols = [SslApplicationProtocol.Http2] });
                negotiatedH2 = inner.NegotiatedApplicationProtocol == SslApplicationProtocol.Http2;
            }
            // 服务端拒绝握手（没有共同协议）也符合合同：只要不协商出 h2。
            catch (System.Security.Authentication.AuthenticationException) { }
            Check(!negotiatedH2, "客户端只报 h2 时服务端绝不能选 h2");
        }
        Check(rig.Connector.Opened.Count == 0 && rig.Gate.Tunnels.All(tunnel => tunnel.Admitted.Count == 0), "握手不合合同时不应准入任何请求");
    }

    /// <summary>代理 CA 带 critical Name Constraints：给两个官方主机签的叶证书链有效；同一个 CA 给别的名字签的证书，链校验报名称约束不允许。叶证书 SAN 只有一个名字。</summary>
    public static Task ProxyAuthorityIsNameConstrained()
    {
        var factory = TestProcess.CertificateFactory;
        using var authority = factory.CreateProxyAuthority([OfficialTargets.ApiHost, OfficialTargets.McpProxyHost]);
        var constraint = authority.Extensions["2.5.29.30"];
        Check(constraint is not null && constraint.Critical, "代理 CA 应带 critical Name Constraints");
        foreach (var (host, allowed) in new[] { (OfficialTargets.ApiHost, true), (OfficialTargets.McpProxyHost, true), ("evil.example.com", false), ("anthropic.com", false) })
        {
            using var leaf = factory.IssueProxyLeaf(authority, host);
            var san = leaf.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
            Check(san.EnumerateDnsNames().SequenceEqual([host]) && !san.EnumerateIPAddresses().Any(), "叶证书 SAN 只有一个 DNS 名：" + host);
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(authority);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            var built = chain.Build(leaf);
            var refusedByName = chain.ChainStatus.Any(status => status.Status == X509ChainStatusFlags.HasNotPermittedNameConstraint);
            Check(allowed ? built : !built && refusedByName, $"{host} 的链校验应为 {(allowed ? "有效" : "名称约束不允许")}，实际 {built} {string.Join(',', chain.ChainStatus.Select(status => status.Status))}");
        }
        return Task.CompletedTask;
    }

    /// <summary>会话关闭（停止、撤销、维护）时，已建立的隧道立刻断开，不等空闲超时。</summary>
    public static async Task SessionCloseDropsTunnel()
    {
        await using var rig = await SiyuProxyRig.Start(idleSeconds: 60);
        var (tcp, inner) = await rig.OpenInner(OfficialHost.Api);
        using (tcp)
        await using (inner)
        {
            var started = DateTime.UtcNow;
            rig.Gate.Tunnels.Single().Close();
            var closed = false;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                closed = await new Http1Reader(inner).ReadHeadAsync(timeout.Token) is null;
            }
            // 服务端关闭连接时客户端可能读到 0 字节，也可能是断开异常；两种都算断开。超时（OperationCanceledException）不接，让用例失败。
            catch (IOException) { closed = true; }
            Check(closed && DateTime.UtcNow - started < TimeSpan.FromSeconds(3), "会话关闭后隧道应在 3 秒内断开（空闲超时是 60 秒，不能靠它）");
        }
    }
}
