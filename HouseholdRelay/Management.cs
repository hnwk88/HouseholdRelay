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
    public RelayConfiguration Read() { var value = ConfigurationFiles.Read<RelayConfiguration>(ConfigPath); value.Validate(); return value; }

    public async Task Initialize(string host, int publicPort, int listenPort)
    {
        if (File.Exists(ConfigPath)) throw new InvalidOperationException("已经初始化，请使用 account / clients 命令管理。");
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown || host.Contains(':') || publicPort is < 1 or > 65535 || listenPort is < 1024 or > 65535)
            throw new InvalidOperationException("请输入有效域名或 IPv4 地址，端口必须有效。");
        var userAgent = await ClientLauncher.DetectUserAgent();
        WindowsSecrets.ProtectDirectory(State); WindowsSecrets.ProtectDirectory(Path.Combine(Home, "clients"));
        using var certificate = CreateCertificate(host, out var authorityPem);
        var pfx = certificate.Export(X509ContentType.Pfx);
        try { WindowsSecrets.Save(Path.Combine(State, "tls.dpapi"), pfx); } finally { CryptographicOperations.ZeroMemory(pfx); }
        File.WriteAllText(Path.Combine(State, "relay-ca.pem"), authorityPem, Encoding.ASCII);
        var config = new RelayConfiguration { PublicUrl = $"https://{host}:{publicPort}", ListenPort = listenPort, UpstreamUserAgent = userAgent };
        foreach (var name in new[] { "study", "bedroom", "office" })
        {
            var token = ConfigurationFiles.NewToken(); config.Clients.Add(new ClientAccess(name, ConfigurationFiles.Hash(token))); ExportClient(name, token, config);
        }
        config.Validate(); ConfigurationFiles.Write(ConfigPath, config);
        Console.WriteLine($"初始化完成。花生壳 TCP {publicPort} → 本机 TCP {listenPort}；对外地址：{config.PublicUrl}");
        Console.WriteLine("已生成 study、bedroom、office 三个独立客户端包。下一步执行 account，然后 serve。");
        Console.WriteLine($"证书 SHA-256：{certificate.GetCertHashString(HashAlgorithmName.SHA256)}");
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
        using var issued = request.Create(authority, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(2), RandomNumberGenerator.GetBytes(16));
        return issued.CopyWithPrivateKey(rsa);
    }

    // Windows Schannel needs a user key container. Without PersistKeySet the framework
    // removes its temporary key on disposal; no certificate is installed in a trust store.
    public static X509Certificate2 LoadServerCertificate(byte[] pfx) =>
        X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet);

    public void SetAccount()
    {
        _ = Read();
        if (Console.IsInputRedirected) throw new InvalidOperationException("请在本机交互终端输入令牌，不接受命令行参数或重定向输入。");
        Console.WriteLine("请粘贴 claude setup-token 生成的账号令牌。输入隐藏，按回车保存：");
        var characters = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (characters.Count > 0) characters.RemoveAt(characters.Count - 1); }
            else if (!char.IsControl(key.KeyChar)) characters.Add(key.KeyChar);
        }
        var text = new string(characters.ToArray()).Trim(); characters.Clear();
        if (text.Length < 16 || text.Length > 8192) throw new InvalidOperationException("令牌长度错误。");
        var bytes = Encoding.UTF8.GetBytes(text);
        try { WindowsSecrets.Save(Path.Combine(State, "account.dpapi"), bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        Console.WriteLine("已用当前 Windows 用户的 DPAPI 加密保存。重启服务后生效。");
    }

    public void ManageClient(string action, string? name)
    {
        var config = Read();
        if (action == "list")
        {
            foreach (var item in config.Clients) Console.WriteLine($"{item.Name,-12} {(item.Enabled ? "启用" : "停用")}");
            return;
        }
        if (name is null || !RelayConfiguration.IsClientName(name)) throw new InvalidOperationException("客户端名称应为小写字母、数字、短横线或下划线，最多 32 字符。");
        var index = config.Clients.FindIndex(c => c.Name == name);
        if (action == "add")
        {
            if (index >= 0 || config.Clients.Count >= 8) throw new InvalidOperationException("名称已存在，或客户端数量已达到 8 个。");
            var token = ConfigurationFiles.NewToken(); config.Clients.Add(new ClientAccess(name, ConfigurationFiles.Hash(token))); ExportClient(name, token, config);
        }
        else
        {
            if (index < 0) throw new InvalidOperationException("没有这个客户端。");
            if (action == "rotate")
            {
                var token = ConfigurationFiles.NewToken(); config.Clients[index] = new ClientAccess(name, ConfigurationFiles.Hash(token)); ExportClient(name, token, config);
            }
            else if (action is "enable" or "disable") config.Clients[index] = config.Clients[index] with { Enabled = action == "enable" };
            else throw new InvalidOperationException("clients 支持 list / add / rotate / enable / disable。");
        }
        ConfigurationFiles.Write(ConfigPath, config); Console.WriteLine("已保存，运行中的服务会在约 0.5 秒内更新客户端访问权限。");
    }

    private void ExportClient(string name, string token, RelayConfiguration config)
    {
        var directory = Path.Combine(Home, "clients", name); Directory.CreateDirectory(directory);
        ConfigurationFiles.Write(Path.Combine(directory, "client.json"), new ClientBundle(config.PublicUrl, token));
        File.Copy(Path.Combine(State, "relay-ca.pem"), Path.Combine(directory, "relay-ca.pem"), true);
        foreach (var suffix in new[] { ".exe", ".dll", ".deps.json", ".runtimeconfig.json" })
        {
            var file = "HouseholdRelay" + suffix; var source = Path.Combine(AppContext.BaseDirectory, file);
            if (File.Exists(source)) File.Copy(source, Path.Combine(directory, file), true);
        }
    }

    public async Task Serve()
    {
        var config = Read();
        var tokenBytes = WindowsSecrets.Load(Path.Combine(State, "account.dpapi"));
        var token = Encoding.UTF8.GetString(tokenBytes); CryptographicOperations.ZeroMemory(tokenBytes);
        var certificateBytes = WindowsSecrets.Load(Path.Combine(State, "tls.dpapi"));
        using var certificate = LoadServerCertificate(certificateBytes);
        CryptographicOperations.ZeroMemory(certificateBytes);
        using var relay = new RelayServer(config, token, data => Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(data)));
        await using var app = relay.Build(certificate);
        await app.StartAsync();
        Console.WriteLine($"HTTPS 已启动：本机 {config.ListenPort}，公网 {config.PublicUrl}。按 Ctrl+C 停止。");
        using var stop = new CancellationTokenSource();
        using var registration = app.Lifetime.ApplicationStopping.Register(stop.Cancel);
        var watcher = WatchClients(relay, stop.Token);
        try { await app.WaitForShutdownAsync(); }
        finally { stop.Cancel(); await watcher; }
    }

    private async Task WatchClients(RelayServer relay, CancellationToken stop)
    {
        var modified = File.GetLastWriteTimeUtc(ConfigPath); using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try
        {
            while (await timer.WaitForNextTickAsync(stop))
            {
                var current = File.GetLastWriteTimeUtc(ConfigPath); if (current == modified) continue;
                modified = current;
                try { relay.Clients.Replace(ConfigurationFiles.Read<RelayConfiguration>(ConfigPath).Clients); Console.WriteLine("{\"event\":\"clients_reloaded\"}"); }
                catch { relay.Clients.DisableAll(); Console.WriteLine("{\"event\":\"invalid_config_clients_disabled\"}"); }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
}
