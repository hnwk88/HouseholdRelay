using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Application.Interfaces.Siyu;
using Core.Siyu;
using Infrastructure.ExternalSources.Siyu;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Tests.TestSuite;

namespace Tests;

/// <summary>思域代理回环测试的搭台：本机回环随机端口起 Kestrel，外层 TLS 用新签的测试站点证书，内层叶证书由新建的代理 CA 签；客户端两层 TLS 都只信这两张测试 CA。不装任何系统证书。</summary>
public sealed class SiyuProxyRig : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly string _siteAuthorityPem;
    private readonly X509Certificate2 _proxyAuthority;

    /// <summary>由 <see cref="Start"/> 构造。</summary>
    private SiyuProxyRig(WebApplication app, int port, string siteAuthorityPem, X509Certificate2 proxyAuthority, FakeSiyuGate gate, FakeOfficialConnector connector)
    {
        _app = app; Port = port; _siteAuthorityPem = siteAuthorityPem; _proxyAuthority = proxyAuthority; Gate = gate; Connector = connector;
    }

    /// <summary>监听端口。</summary>
    public int Port { get; }

    /// <summary>假准入。</summary>
    public FakeSiyuGate Gate { get; }

    /// <summary>假出站连接器。</summary>
    public FakeOfficialConnector Connector { get; }

    /// <summary>起一套：请求体上限 10000 字节，空闲超时默认 5 秒。</summary>
    public static async Task<SiyuProxyRig> Start(int idleSeconds = 5)
    {
        var factory = TestProcess.CertificateFactory;
        using var issued = factory.CreateCertificate("localhost", out var sitePem);
        var site = factory.LoadServerCertificate(issued.Export(X509ContentType.Pkcs12));
        var proxyAuthority = factory.CreateProxyAuthority([OfficialTargets.ApiHost, OfficialTargets.McpProxyHost]);
        var leaves = new Dictionary<OfficialHost, X509Certificate2>
        {
            [OfficialHost.Api] = factory.IssueProxyLeaf(proxyAuthority, OfficialTargets.ApiHost),
            [OfficialHost.McpProxy] = factory.IssueProxyLeaf(proxyAuthority, OfficialTargets.McpProxyHost),
        };
        var gate = new FakeSiyuGate(); var connector = new FakeOfficialConnector();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.SetMinimumLevel(LogLevel.Critical);
        builder.Services.AddSingleton<ISiyuGate>(gate);
        builder.Services.AddSingleton<IOfficialConnector>(connector);
        builder.Services.AddSingleton<ISiyuProxyCertificates>(new FixedSiyuProxyCertificates(leaves));
        builder.Services.AddSingleton(SiyuProxyLimits.Default(10_000) with { IdleTimeout = TimeSpan.FromSeconds(idleSeconds) });
        builder.Services.AddSingleton<SiyuProxyConnectionHandler>();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen =>
        {
            // 外层只说 HTTP/1.1：ALPN 只报 http/1.1，客户端才会在外层发 CONNECT 而不是 HTTP/2。
            listen.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1;
            listen.UseHttps(site);
            listen.UseConnectionHandler<SiyuProxyConnectionHandler>();
        }));
        var app = builder.Build();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new SiyuProxyRig(app, new Uri(address).Port, sitePem, proxyAuthority, gate, connector);
    }

    /// <summary>连上并完成外层 TLS（只信测试站点 CA，名字必须是 localhost）。</summary>
    public async Task<(TcpClient Tcp, SslStream Outer)> OpenOuter()
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, Port);
        var outer = new SslStream(tcp.GetStream(), false);
        await outer.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "localhost",
            RemoteCertificateValidationCallback = (_, certificate, _, _) => Trusted(certificate, X509Certificate2.CreateFromPem(_siteAuthorityPem), "localhost"),
        });
        return (tcp, outer);
    }

    /// <summary>发合法 CONNECT 并确认 200。</summary>
    public static async Task Connect(Stream outer, OfficialHost host)
    {
        var authority = OfficialTargets.Authority(host);
        await outer.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT {authority} HTTP/1.1\r\nHost: {authority}\r\nProxy-Authorization: Basic {Basic(FakeSiyuGate.SessionId, FakeSiyuGate.Ticket)}\r\n\r\n"));
        await outer.FlushAsync();
        var raw = await ReadHead(outer);
        Check(raw.StartsWith("HTTP/1.1 200 ", StringComparison.Ordinal), "CONNECT 应回 200：" + raw);
    }

    /// <summary>外层、CONNECT、内层 TLS（只信代理 CA，名字必须是该官方主机，ALPN http/1.1）一次做完。</summary>
    public async Task<(TcpClient Tcp, SslStream Inner)> OpenInner(OfficialHost host)
    {
        var (tcp, outer) = await OpenOuter();
        await Connect(outer, host);
        var inner = new SslStream(outer, false);
        await inner.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = OfficialTargets.Name(host),
            ApplicationProtocols = [SslApplicationProtocol.Http11],
            RemoteCertificateValidationCallback = (_, certificate, _, _) => Trusted(certificate, _proxyAuthority, OfficialTargets.Name(host)),
        });
        return (tcp, inner);
    }

    /// <summary>读一个完整回应（头加体）原样写进 <paramref name="sink"/>。</summary>
    public static async Task ReadResponse(Http1Reader reader, string method, Stream sink)
    {
        var raw = await reader.ReadHeadAsync(CancellationToken.None) ?? throw new EndOfStreamException("没有回应。");
        var head = Http1ResponseHead.Parse(raw, method);
        await sink.WriteAsync(raw);
        switch (head.Framing)
        {
            case Http1BodyFraming.Length: await reader.CopyExactAsync(head.ContentLength, sink, CancellationToken.None); break;
            case Http1BodyFraming.Chunked: await reader.CopyChunkedAsync(sink, CancellationToken.None); break;
            case Http1BodyFraming.UntilClose: await reader.CopyToEndAsync(sink, CancellationToken.None); break;
            case Http1BodyFraming.None: break;
        }
    }

    /// <summary>读一个回应，给出解析后的头和 UTF-8 正文。</summary>
    public static async Task<(Http1ResponseHead Head, string Body)> ReadOne(Http1Reader reader, string method)
    {
        var sink = new MemoryStream();
        await ReadResponse(reader, method, sink);
        var all = sink.ToArray();
        var split = all.AsSpan().IndexOf("\r\n\r\n"u8) + 4;
        return (Http1ResponseHead.Parse(all[..split], method), Encoding.UTF8.GetString(all, split, all.Length - split));
    }

    /// <summary>Basic 凭据的 base64。</summary>
    public static string Basic(string user, string password) => Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password));

    /// <summary>停掉宿主。</summary>
    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        _proxyAuthority.Dispose();
    }

    /// <summary>逐字节读到第一个空行为止（CONNECT 回应头后面不能多读，内层 TLS 字节紧跟其后）。</summary>
    private static async Task<string> ReadHead(Stream stream)
    {
        var bytes = new List<byte>(); var one = new byte[1];
        while (bytes.Count < 8192)
        {
            if (await stream.ReadAsync(one) == 0) break;
            bytes.Add(one[0]);
            if (bytes.Count >= 4 && bytes[^4] == '\r' && bytes[^3] == '\n' && bytes[^2] == '\r' && bytes[^1] == '\n') break;
        }
        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    /// <summary>只信给定 CA、名字必须匹配的证书校验。</summary>
    private static bool Trusted(X509Certificate? certificate, X509Certificate2 authority, string name)
    {
        if (certificate is null) return false;
        using var leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(authority);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(leaf) && leaf.MatchesHostname(name);
    }
}
