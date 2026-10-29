using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using HouseholdRelay;
using Microsoft.AspNetCore.Http;
using static Tests.TestSuite;

namespace Tests;

static class UpstreamSecurityTests
{
    /// <summary>
    /// 验证上游连接、私有地址、TLS 与取消边界：对明文端口用 HTTPS、关闭的端口、云元数据地址（含 IPv4 映射的 IPv6 写法）、
    /// 不可达的文档保留地址都不能通过诊断；未允许私网时 localhost 即使用 HTTPS 也在连接前被拒且上游收不到请求；
    /// 不受信任的 TLS 证书在发出凭据前就失败；诊断被取消时结果为已取消、候选记为失败；全程当前上游保持未配置。
    /// </summary>
    public static async Task NetworkBoundaries()
    {
        using var home = new TestHome(); var service = new TestProcess(home.Path).Upstreams;
        await using var plain = await UpstreamFixture.Create();
        foreach (var url in new[] { plain.Url.Replace("http://", "https://", StringComparison.Ordinal), "http://127.0.0.1:1", "http://169.254.169.254", "http://[::ffff:169.254.169.254]", "http://192.0.2.1" })
        {
            var id = service.SaveCandidate(service.ReadActive().Version, plain.Options() with { BaseUrl = url }, "fixture-network-secret");
            using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var result = await service.TestCandidate(id, true, bound.Token); Check(!result.CanActivate);
        }
        // localhost 也走真实 DNS 和连接前地址校验，不能以 HTTPS 绕过私网显式选择。
        var denied = service.SaveCandidate(service.ReadActive().Version, plain.Options() with { BaseUrl = plain.Url.Replace("127.0.0.1", "localhost").Replace("http:", "https:"), AllowPrivateNetwork = false }, "fixture-private-secret");
        var refused = await service.TestCandidate(denied, true, CancellationToken.None); Check(refused.Connection == "failed" && plain.Calls.IsEmpty);
        using var generated = TestProcess.CertificateFactory.CreateCertificate("localhost", out _);
        using var certificate = TestProcess.CertificateFactory.LoadServerCertificate(generated.Export(X509ContentType.Pfx));
        await using var untrusted = await UpstreamFixture.Create(certificate);
        var tls = service.SaveCandidate(service.ReadActive().Version, untrusted.Options(), "fixture-tls-secret");
        var failedTls = await service.TestCandidate(tls, true, CancellationToken.None);
        Check(failedTls.Connection == "failed" && untrusted.Calls.IsEmpty, "Untrusted TLS must fail before sending credentials");
        plain.Handler = async context => { try { await Task.Delay(Timeout.Infinite, context.RequestAborted); } catch (OperationCanceledException) { /* 模拟永不应答的上游；诊断取消后请求中止属于预期，安静结束即可。 */ } };
        var slow = service.SaveCandidate(service.ReadActive().Version, plain.Options(), "fixture-slow-secret");
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var cancelled = await service.TestCandidate(slow, true, cancel.Token); Check(cancelled.Call == "cancelled" && service.ReadCandidate(slow).State == "failed");
        Check(!service.ReadActive().Configured);
    }

    /// <summary>
    /// 验证证书导入、DPAPI 保存、重启与真实 TLS 服务：密码错误、缺少受信根、主机名不符、版本过期、证书已过期都被拒绝且不留下任何证书；
    /// 合规证书导入后重启可读出两张证书（仅叶子带私钥），并能以完整链在真实 TLS 上提供健康检查；
    /// 数据库中保存的是加密后的字节；把公开地址改成证书不覆盖的主机被拒绝；旧版 TLS 配置提供服务时也带上 CA 链。
    /// </summary>
    public static async Task CertificateImport()
    {
        using var home = new TestHome(); var process = new TestProcess(home.Path); var database = process.Database; var settings = process.Settings; var certificates = process.Certificates;
        using var generated = TestProcess.CertificateFactory.CreateCertificate("localhost", out var pem); using var authority = X509Certificate2.CreateFromPem(pem);
        var roots = new X509Certificate2Collection(authority); var bundle = new X509Certificate2Collection(generated); bundle.Add(authority);
        var bytes = bundle.Export(X509ContentType.Pkcs12, "fixture-pfx-password")!;
        CheckRejected(() => certificates.Import(settings.Read().Version, "https://localhost", 8787, bytes, "wrong-password"));
        CheckRejected(() => certificates.Import(settings.Read().Version, "https://localhost", 8787, bytes, "fixture-pfx-password"));
        CheckRejected(() => certificates.Import(settings.Read().Version, "https://different.example", 8787, bytes, "fixture-pfx-password", roots));
        CheckRejected(() => certificates.Import(settings.Read().Version + 1, "https://localhost", 8787, bytes, "fixture-pfx-password", roots));
        using var expiredKey = RSA.Create(2048);
        var expiredRequest = new CertificateRequest("CN=localhost", expiredKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); expiredRequest.CertificateExtensions.Add(san.Build());
        using var expired = expiredRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(-1));
        CheckRejected(() => certificates.Import(settings.Read().Version, "https://localhost", 8787, expired.Export(X509ContentType.Pkcs12), null));
        Check(!settings.HasCertificate && certificates.ReadInfo() is null);
        certificates.Import(settings.Read().Version, "https://localhost", 8787, bytes, "fixture-pfx-password", roots);
        Check(certificates.ReadInfo()?.Host == "localhost" && settings.HasCertificate);
        var restarted = new TestProcess(home.Path); var stored = restarted.Settings.LoadCertificate()!;
        var collection = X509CertificateLoader.LoadPkcs12Collection(stored, null, X509KeyStorageFlags.EphemeralKeySet);
        try
        {
            Check(collection.Count == 2 && collection.Cast<X509Certificate2>().Count(item => item.HasPrivateKey) == 1);
            using var leaf = TestProcess.CertificateFactory.LoadServerCertificate(stored);
            var client = new ClientRegistry([new("certificate-test", ConfigurationFiles.Hash("certificate-personal-token"))]);
            using var relayServices = TestProcess.StandaloneRelay(new RelayConfiguration(), null, clients: client); var relay = relayServices.GetRequiredService<RelayServer>();
            await using var app = relay.Build(leaf, 0, collection); await app.StartAsync();
            using var http = new HttpClient(new SocketsHttpHandler
            {
                UseProxy = false,
                SslOptions = new() { RemoteCertificateValidationCallback = (_, remote, _, errors) =>
                {
                    if (remote is null || (errors & System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch) != 0) return false;
                    using var remoteCertificate = new X509Certificate2(remote); using var chain = new X509Chain();
                    chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust; chain.ChainPolicy.CustomTrustStore.Add(authority);
                    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck; return chain.Build(remoteCertificate);
                } }
            });
            using var health = await http.GetAsync(app.Urls.Single() + "/healthz"); Check(health.IsSuccessStatusCode);
        }
        // 无论成败都释放证书对象并清零读出的私钥字节，避免密钥材料留在内存中。
        finally { foreach (var item in collection) item.Dispose(); CryptographicOperations.ZeroMemory(stored); }
        using var connection = database.Open();
        var protectedBytes = (byte[])RelayDatabase.Scalar(connection, null, "SELECT protected FROM secrets WHERE name='tls'")!;
        Check(!protectedBytes.SequenceEqual(bytes));
        var publicUrl = settings.Read(); publicUrl.Value.PublicUrl = "https://different.example";
        CheckRejected(() => settings.Save(publicUrl.Version, publicUrl.Value));
        using var legacyHome = new TestHome(); var legacyDatabase = new TestProcess(legacyHome.Path);
        legacyDatabase.Settings.ConfigureLegacyTls("localhost", 5433, 8787);
        using var legacyChain = legacyDatabase.Certificates.LoadForServing()!;
        Check(legacyChain.Chain.Count == 2 && legacyChain.Chain.Cast<X509Certificate2>().Any(item => !item.HasPrivateKey), "Legacy CA must accompany its leaf when serving TLS");
    }

    /// <summary>断言给定的证书或配置操作以 InvalidOperationException 被拒绝，否则判失败。</summary>
    private static void CheckRejected(Action action)
    { var rejected = false; try { action(); } catch (InvalidOperationException) { /* 只把预期的拒绝异常记为成功，其他异常照常抛出使测试失败。 */ rejected = true; } Check(rejected, "Expected invalid certificate configuration to be rejected"); }
}
