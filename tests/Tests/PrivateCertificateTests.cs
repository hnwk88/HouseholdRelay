using System.IO.Compression;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using HouseholdRelay;
using static Tests.TestSuite;

namespace Tests;

/// <summary>私有证书的保存、网页权限与真正 TLS 校验反例。</summary>
static class PrivateCertificateTests
{
    /// <summary>
    /// 验证私有证书生命周期、版本冲突和过期证书拒绝启动：创建后得到 64 位指纹且模式为私有；用过期版本号或未声明替换再次创建都被拒，根证书不变；
    /// 新进程加载到带私钥的叶证书和两级证书链；主机名不符的验证被拒；把保存的证书换成过期证书后加载服务证书被拒；
    /// 显式替换后得到新的根证书并能正常加载。
    /// </summary>
    public static Task Lifecycle()
    {
        using var home = new TestHome(); var process = new TestProcess(home.Path); var database = process.Database;
        var settings = process.Settings; var certificates = process.Certificates;
        var version = settings.Read().Version;
        certificates.CreatePrivate(version, "https://localhost:8787", 8787);
        var fingerprint = certificates.AuthorityFingerprint();
        Check(fingerprint?.Length == 64 && certificates.ReadInfo()?.Mode == "private");
        Reject(() => certificates.CreatePrivate(version, "https://localhost:8787", 8787, true));
        Reject(() => certificates.CreatePrivate(settings.Read().Version, "https://localhost:8787", 8787));
        Check(certificates.AuthorityFingerprint() == fingerprint, "Rejected writes must preserve the active root");
        using (var serving = new TestProcess(home.Path).Certificates.LoadForServing()!)
            Check(serving.Certificate.HasPrivateKey && serving.Chain.Count == 2);
        // 与实际启动相同的验证必须拒绝篡改后的地址与过期叶证书。
        using var leaf = TestProcess.CertificateFactory.CreateCertificate("localhost", out var ca);
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

    /// <summary>
    /// 验证网页创建私有证书、CSRF 与仅公开内容的信任包下载：未登录下载信任包被重定向；缺少防伪令牌的创建返回 400 且不生成证书；
    /// 带令牌创建成功，重复提交同一（已过期版本的）表单不会更换根证书；下载响应禁止缓存，压缩包恰好含安装脚本、说明和 relay-ca.pem，
    /// 其中根证书指纹匹配且不含任何私钥。
    /// </summary>
    public static async Task WebJourney()
    {
        await using var web = await WebFixture.Create(); var settings = web.Settings;
        using (var anonymous = await web.Http.GetAsync(web.Url + "/Connection?handler=TrustPackage")) Check(anonymous.StatusCode == HttpStatusCode.Redirect);
        await web.Login();
        var values = new Dictionary<string, string> { ["Version"] = settings.Read().Version.ToString(), ["PublicUrl"] = "https://localhost:8787", ["ListenPort"] = "8787" };
        using (var forged = await web.Post("/Https?handler=Private", values)) Check(forged.StatusCode == HttpStatusCode.BadRequest);
        Check(!settings.HasCertificate);
        values["__RequestVerificationToken"] = await web.Csrf("/Https");
        using var created = await web.Post("/Https?handler=Private", values);
        Check(created.IsSuccessStatusCode && settings.HasCertificate);
        var certificates = web.Certificates; var fingerprint = certificates.AuthorityFingerprint();
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

    /// <summary>
    /// 验证私有证书在真实 TLS 连接上的信任边界：中转以私有证书和证书链提供服务；只用系统信任的客户端连接失败，
    /// 信任本私有 CA 的客户端健康检查成功，信任另一个 CA 或以不相关主机名连接都失败。
    /// </summary>
    public static async Task Transport()
    {
        using var home = new TestHome(); var process = new TestProcess(home.Path); var database = process.Database;
        var settings = process.Settings; var certificates = process.Certificates;
        certificates.CreatePrivate(settings.Read().Version, "https://localhost:8787", 8787);
        using var serving = certificates.LoadForServing()!;
        using var relayServices = TestProcess.StandaloneRelay(settings.Read().Value, null); var relay = relayServices.GetRequiredService<RelayServer>();
        await using var app = relay.Build(serving.Certificate, 0, serving.Chain); await app.StartAsync();
        var url = app.Urls.Single();
        using var untrusted = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        await RejectTls(untrusted, url);
        using var authority = X509Certificate2.CreateFromPem(certificates.ReadAuthorityPem()!);
        using var valid = Client(authority); using var response = await valid.GetAsync(url + "/healthz"); Check(response.IsSuccessStatusCode);
        using var otherLeaf = TestProcess.CertificateFactory.CreateCertificate("localhost", out var otherCa);
        using var otherRoot = X509Certificate2.CreateFromPem(otherCa); using var wrongRoot = Client(otherRoot);
        await RejectTls(wrongRoot, url);
        using var wrongName = Client(authority, "unrelated.invalid"); await RejectTls(wrongName, url);
    }

    /// <summary>
    /// 创建只信任指定根证书的 HTTPS 客户端，可指定 TLS 主机名：名称不符或无证书即拒绝，
    /// 证书链只以该根为信任锚、不查吊销、不下载中间证书，并要求叶证书带服务器身份验证用途。
    /// </summary>
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
    /// <summary>断言该客户端访问健康检查时在真实 TLS 握手阶段失败（抛出请求异常），握手成功即判失败。</summary>
    private static async Task RejectTls(HttpClient client, string url)
    {
        var rejected = false;
        try { using var response = await client.GetAsync(url + "/healthz"); }
        // 证书不受信任或主机名不符时握手失败会抛出请求异常，这正是预期结果，记为拒绝成功。
        catch (HttpRequestException) { rejected = true; }
        Check(rejected, "Invalid TLS must fail the actual connection");
    }
    /// <summary>断言给定的证书操作以 InvalidOperationException 被拒绝，否则判失败。</summary>
    private static void Reject(Action action)
    { var failed = false; try { action(); } catch (InvalidOperationException) { /* 只把预期的拒绝异常记为成功，其他异常照常抛出使测试失败。 */ failed = true; } Check(failed); }
}
