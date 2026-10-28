using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using HouseholdRelay;
using Microsoft.AspNetCore.Http;
using static Tests;

static class UpstreamSecurityTests
{
    public static async Task NetworkBoundaries()
    {
        using var home = new TestHome(); var service = new UpstreamService(new RelayDatabase(home.Path));
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
        using var generated = Management.CreateCertificate("localhost", out _);
        using var certificate = Management.LoadServerCertificate(generated.Export(X509ContentType.Pfx));
        await using var untrusted = await UpstreamFixture.Create(certificate);
        var tls = service.SaveCandidate(service.ReadActive().Version, untrusted.Options(), "fixture-tls-secret");
        var failedTls = await service.TestCandidate(tls, true, CancellationToken.None);
        Check(failedTls.Connection == "failed" && untrusted.Calls.IsEmpty, "Untrusted TLS must fail before sending credentials");
        plain.Handler = async context => { try { await Task.Delay(Timeout.Infinite, context.RequestAborted); } catch (OperationCanceledException) { } };
        var slow = service.SaveCandidate(service.ReadActive().Version, plain.Options(), "fixture-slow-secret");
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var cancelled = await service.TestCandidate(slow, true, cancel.Token); Check(cancelled.Call == "cancelled" && service.ReadCandidate(slow).State == "failed");
        Check(!service.ReadActive().Configured);
    }

    public static async Task CertificateImport()
    {
        using var home = new TestHome(); var database = new RelayDatabase(home.Path); var settings = new ConfigurationService(database); var certificates = new CertificateService(database);
        using var generated = Management.CreateCertificate("localhost", out var pem); using var authority = X509Certificate2.CreateFromPem(pem);
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
        var restarted = new RelayDatabase(home.Path); var stored = new ConfigurationService(restarted).LoadCertificate()!;
        var collection = X509CertificateLoader.LoadPkcs12Collection(stored, null, X509KeyStorageFlags.EphemeralKeySet);
        try
        {
            Check(collection.Count == 2 && collection.Cast<X509Certificate2>().Count(item => item.HasPrivateKey) == 1);
            using var leaf = Management.LoadServerCertificate(stored);
            var client = new ClientRegistry([new("certificate-test", ConfigurationFiles.Hash("certificate-personal-token"))]);
            using var relay = new RelayServer(new RelayConfiguration(), null, clients: client);
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
        finally { foreach (var item in collection) item.Dispose(); CryptographicOperations.ZeroMemory(stored); }
        using var connection = database.Open();
        var protectedBytes = (byte[])RelayDatabase.Scalar(connection, null, "SELECT protected FROM secrets WHERE name='tls'")!;
        Check(!protectedBytes.SequenceEqual(bytes));
        var publicUrl = settings.Read(); publicUrl.Value.PublicUrl = "https://different.example";
        CheckRejected(() => settings.Save(publicUrl.Version, publicUrl.Value));
        using var legacyHome = new TestHome(); var legacyDatabase = new RelayDatabase(legacyHome.Path);
        new ConfigurationService(legacyDatabase).ConfigureLegacyTls("localhost", 5433, 8787);
        using var legacyChain = new CertificateService(legacyDatabase).LoadForServing()!;
        Check(legacyChain.Chain.Count == 2 && legacyChain.Chain.Cast<X509Certificate2>().Any(item => !item.HasPrivateKey), "Legacy CA must accompany its leaf when serving TLS");
    }

    private static void CheckRejected(Action action)
    { var rejected = false; try { action(); } catch (InvalidOperationException) { rejected = true; } Check(rejected, "Expected invalid certificate configuration to be rejected"); }
}
