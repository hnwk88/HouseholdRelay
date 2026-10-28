using System.IO.Compression;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using HouseholdRelay;
using static Tests;

/// <summary>私有证书的保存、网页权限与真正 TLS 校验反例。</summary>
static class PrivateCertificateTests
{
    public static Task Lifecycle()
    {
        using var home = new TestHome(); var database = new RelayDatabase(home.Path);
        var settings = new ConfigurationService(database); var certificates = new CertificateService(database);
        var version = settings.Read().Version;
        certificates.CreatePrivate(version, "https://localhost:8787", 8787);
        var fingerprint = certificates.AuthorityFingerprint();
        Check(fingerprint?.Length == 64 && certificates.ReadInfo()?.Mode == "private");
        Reject(() => certificates.CreatePrivate(version, "https://localhost:8787", 8787, true));
        Reject(() => certificates.CreatePrivate(settings.Read().Version, "https://localhost:8787", 8787));
        Check(certificates.AuthorityFingerprint() == fingerprint, "Rejected writes must preserve the active root");
        using (var serving = new CertificateService(new RelayDatabase(home.Path)).LoadForServing()!)
            Check(serving.Certificate.HasPrivateKey && serving.Chain.Count == 2);
        // 与实际启动相同的验证必须拒绝篡改后的地址与过期叶证书。
        using var leaf = Management.CreateCertificate("localhost", out var ca);
        using var root = X509Certificate2.CreateFromPem(ca);
        var roots = new X509Certificate2Collection(root);
        Reject(() => CertificateService.Validate(leaf, roots, "wrong.example", roots));
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=expired", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); request.CertificateExtensions.Add(san.Build());
        using var expired = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(-1));
        database.Write((connection, transaction) => RelayDatabase.Execute(connection, transaction,
            "UPDATE secrets SET protected=$value WHERE name='tls'", ("$value", WindowsSecrets.Protect(expired.Export(X509ContentType.Pkcs12)))));
        Reject(() => certificates.LoadForServing()?.Dispose());
        certificates.CreatePrivate(settings.Read().Version, "https://localhost:8787", 8787, true);
        Check(certificates.AuthorityFingerprint() != fingerprint, "Replacement must advertise a new root");
        using var restored = certificates.LoadForServing(); Check(restored is not null);
        return Task.CompletedTask;
    }

    public static async Task WebJourney()
    {
        await using var web = await WebFixture.Create(); var settings = new ConfigurationService(web.Database);
        using (var anonymous = await web.Http.GetAsync(web.Url + "/Connection?handler=TrustPackage")) Check(anonymous.StatusCode == HttpStatusCode.Redirect);
        await web.Login();
        var values = new Dictionary<string, string> { ["Version"] = settings.Read().Version.ToString(), ["PublicUrl"] = "https://localhost:8787", ["ListenPort"] = "8787" };
        using (var forged = await web.Post("/Https?handler=Private", values)) Check(forged.StatusCode == HttpStatusCode.BadRequest);
        Check(!settings.HasCertificate);
        values["__RequestVerificationToken"] = await web.Csrf("/Https");
        using var created = await web.Post("/Https?handler=Private", values);
        Check(created.IsSuccessStatusCode && settings.HasCertificate);
        var certificates = new CertificateService(web.Database); var fingerprint = certificates.AuthorityFingerprint();
        using var stale = await web.Post("/Https?handler=Private", values);
        Check(certificates.AuthorityFingerprint() == fingerprint);
        using var download = await web.Http.GetAsync(web.Url + "/Connection?handler=TrustPackage");
        Check(download.IsSuccessStatusCode && download.Headers.CacheControl?.NoStore == true);
        using var bytes = new MemoryStream(await download.Content.ReadAsByteArrayAsync()); using var zip = new ZipArchive(bytes);
        Check(zip.Entries.Select(entry => entry.Name).Order().SequenceEqual(new[] { "Install-RelayTrust.ps1", "README.txt", "relay-ca.pem" }.Order()));
        using var reader = new StreamReader(zip.GetEntry("relay-ca.pem")!.Open()); var pem = await reader.ReadToEndAsync();
        using var root = X509Certificate2.CreateFromPem(pem);
        Check(!root.HasPrivateKey && root.GetCertHashString(HashAlgorithmName.SHA256) == fingerprint);
        Check(!pem.Contains("PRIVATE KEY", StringComparison.Ordinal));
    }

    public static async Task Transport()
    {
        using var home = new TestHome(); var database = new RelayDatabase(home.Path);
        var settings = new ConfigurationService(database); var certificates = new CertificateService(database);
        certificates.CreatePrivate(settings.Read().Version, "https://localhost:8787", 8787);
        using var serving = certificates.LoadForServing()!;
        using var relay = new RelayServer(settings.Read().Value, null);
        await using var app = relay.Build(serving.Certificate, 0, serving.Chain); await app.StartAsync();
        var url = app.Urls.Single();
        using var untrusted = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        await RejectTls(untrusted, url);
        using var authority = X509Certificate2.CreateFromPem(certificates.ReadAuthorityPem()!);
        using var valid = Client(authority); using var response = await valid.GetAsync(url + "/healthz"); Check(response.IsSuccessStatusCode);
        using var otherLeaf = Management.CreateCertificate("localhost", out var otherCa);
        using var otherRoot = X509Certificate2.CreateFromPem(otherCa); using var wrongRoot = Client(otherRoot);
        await RejectTls(wrongRoot, url);
        using var wrongName = Client(authority, "unrelated.invalid"); await RejectTls(wrongName, url);
    }

    private static HttpClient Client(X509Certificate2 root, string? targetHost = null) => new(new SocketsHttpHandler
    {
        UseProxy = false,
        SslOptions = new() { TargetHost = targetHost, RemoteCertificateValidationCallback = (_, remote, _, errors) =>
        {
            if (remote is null || (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0) return false;
            using var leaf = new X509Certificate2(remote); using var chain = new X509Chain();
            if (targetHost is not null && !leaf.MatchesHostname(targetHost, false, false)) return false;
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust; chain.ChainPolicy.CustomTrustStore.Add(root);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck; chain.ChainPolicy.DisableCertificateDownloads = true;
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1")); return chain.Build(leaf);
        } }
    });
    private static async Task RejectTls(HttpClient client, string url)
    {
        var rejected = false;
        try { using var response = await client.GetAsync(url + "/healthz"); }
        catch (HttpRequestException) { rejected = true; }
        Check(rejected, "Invalid TLS must fail the actual connection");
    }
    private static void Reject(Action action)
    { var failed = false; try { action(); } catch (InvalidOperationException) { failed = true; } Check(failed); }
}
