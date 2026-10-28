using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace HouseholdRelay;

public sealed class Management(string home)
{
    public string Home { get; } = Path.GetFullPath(home);
    public string State => Path.Combine(Home, ".state");
    public string ConfigPath => Path.Combine(State, "server.json");
    private RelayDatabase? database;
    public RelayDatabase Database => database ??= new RelayDatabase(Home);
    public RelayConfiguration Read()
    {
        var config = new ConfigurationService(Database).Read().Value;
        config.Clients = new IdentityService(Database).ReadAccess().ToList();
        return config;
    }
    public Task Initialize(string host, int publicPort, int listenPort)
    {
        var settings = new ConfigurationService(Database);
        if (settings.HasCertificate) throw new InvalidOperationException("已有证书，请使用现有配置启动服务。");
        settings.ConfigureLegacyTls(host, publicPort, listenPort);
        Console.WriteLine("已保存旧版私有 CA 连接配置。请运行 serve，通过本机网页设置管理员并逐人发放令牌。");
        return Task.CompletedTask;
    }
    public static X509Certificate2 CreateCertificate(string host, out string authorityPem)
    {
        using var authorityKey = RSA.Create(2048);
        var authorityRequest = new CertificateRequest("CN=Household Relay Private CA", authorityKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        authorityRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        authorityRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        authorityRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(authorityRequest.PublicKey, false));
        using var authority = authorityRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(5));
        authorityPem = authority.ExportCertificatePem();
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Household Relay", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); san.AddIpAddress(IPAddress.Loopback);
        if (IPAddress.TryParse(host, out var ip)) { if (!ip.Equals(IPAddress.Loopback)) san.AddIpAddress(ip); }
        else if (host != "localhost") san.AddDnsName(host);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        // 私有 CA 名称可能相同；明确签发者的密钥和序列号，防止 TLS 链选择同名的另一证书。
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(authority, true, true));
        using var issued = request.Create(authority, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(2), RandomNumberGenerator.GetBytes(16));
        return issued.CopyWithPrivateKey(rsa);
    }

    // Windows Schannel needs a user key container. Without PersistKeySet the framework
    // removes its temporary key on disposal; no certificate is installed in a trust store.
    public static X509Certificate2 LoadServerCertificate(byte[] pfx) =>
        X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet);

    public void SetAccount()
    {
        var settings = new ConfigurationService(Database);
        if (Console.IsInputRedirected) throw new InvalidOperationException("请在本机交互终端输入令牌，不接受命令行参数或重定向输入。");
        Console.WriteLine("请粘贴上游访问令牌。输入隐藏，按回车保存：");
        var characters = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (characters.Count > 0) characters.RemoveAt(characters.Count - 1); }
            else if (!char.IsControl(key.KeyChar)) characters.Add(key.KeyChar);
        }
        var token = new string(characters.ToArray()).Trim(); characters.Clear();
        settings.SaveCredential(token);
        Console.WriteLine("上游凭据已加密保存为候选配置。请在本机网页填写模型、测试后激活。");
    }

    public void ManageClient(string action, string? name, string? id = null, string? displayName = null)
    {
        var identities = new IdentityService(Database);
        if (action == "list")
        {
            foreach (var user in identities.List()) Console.WriteLine($"{user.Id}  {user.Name}  {user.DisplayName}  {(user.Enabled ? "启用" : "停用")}");
            return;
        }
        IssuedToken? issued = null;
        if (action == "add") issued = identities.Create(displayName ?? name ?? throw new InvalidOperationException("请提供 --display-name 或 --name。"), name);
        else
        {
            id ??= identities.List().SingleOrDefault(user => user.Name == name)?.Id;
            if (id is null) throw new InvalidOperationException("请用 --id 指定使用者主键，或用 --name 查找旧客户端。");
            switch (action)
            {
                case "rotate": issued = identities.Rotate(id); break;
                case "enable": identities.SetEnabled(id, true); break;
                case "disable": identities.SetEnabled(id, false); break;
                case "rename": identities.Rename(id, displayName ?? throw new InvalidOperationException("请提供 --display-name。")); break;
                default: throw new InvalidOperationException("clients 支持 list / add / rotate / enable / disable / rename。");
            }
        }
        Console.WriteLine("已保存，新的权限检查立即生效。");
        if (issued is not null)
        {
            Console.WriteLine($"使用者：{issued.UserId}；个人令牌仅显示这一次：");
            Console.WriteLine(issued.Token);
        }
    }

    public async Task Serve(int adminPort = 8788)
    {
        if (adminPort is < 1024 or > 65535) throw new InvalidOperationException("管理端口应在 1024 至 65535 之间。");
        var settings = new ConfigurationService(Database); var config = settings.Read().Value;
        var identities = new IdentityService(Database);
        using var relay = new RelayServer(config, null, data => Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(data)), clients: identities.Clients, upstreams: new UpstreamService(Database));
        var certificates = new CertificateService(Database);
        ServerCertificates? loaded = null;
        try { loaded = certificates.LoadForServing(); }
        catch (Exception error) when (error is CryptographicException or InvalidOperationException)
        { Console.WriteLine("HTTPS 证书无法启用，请在本机管理页面检查证书链、域名和有效期后重新导入。"); }
        using var certificate = loaded;
        await using var admin = ManagementWeb.Build(Database, identities, relay, adminPort);
        await using var api = certificate is null ? null : relay.Build(certificate.Certificate, certificateChain: certificate.Chain);
        await admin.StartAsync();
        if (api is not null) { await api.StartAsync(); relay.HttpsActive = true; }
        Console.WriteLine($"本机管理：http://127.0.0.1:{adminPort}；按 Ctrl+C 停止。");
        var bootstrap = new AdministratorService(Database).BootstrapCode();
        if (bootstrap is not null) Console.WriteLine($"首次设置引导码（仅供本机输入）：{bootstrap}");
        Console.WriteLine(api is null ? "对外 HTTPS 未启用，请在本机网页导入可信证书并重启。" : certificates.ReadInfo()?.Mode == "trusted"
            ? $"HTTPS 监听端口：{config.ListenPort}；公网映射是否可达需从外部客户端验证。"
            : $"旧版 HTTPS 监听端口：{config.ListenPort}；需客户端额外信任旧版 CA。");
        await admin.WaitForShutdownAsync();
    }
}
