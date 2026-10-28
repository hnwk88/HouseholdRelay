using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static HouseholdRelay.RelayDatabase;

namespace HouseholdRelay;

public sealed record SettingsSnapshot(long Version, RelayConfiguration Value);

/// <summary>管理配置的唯一写入路径；设置版本冲突须由操作者重新加载后决定。</summary>
public sealed class ConfigurationService(RelayDatabase database)
{
    public SettingsSnapshot Read()
    {
        using var connection = database.Open();
        using var command = Command(connection, null, "SELECT version,json FROM settings WHERE id=1");
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("实例配置缺失，请恢复备份。");
        var config = JsonSerializer.Deserialize<RelayConfiguration>(reader.GetString(1), ConfigurationFiles.Json)
            ?? throw new InvalidOperationException("实例配置损坏。");
        config.Validate();
        return new(reader.GetInt64(0), config);
    }
    public void Save(long version, RelayConfiguration config)
    {
        config.Validate();
        if (config.PublicUrl.Length != 0 && (!Uri.TryCreate(config.PublicUrl, UriKind.Absolute, out var url)
            || url.Scheme != "https" || url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0 || url.AbsolutePath != "/"))
            throw new InvalidOperationException("对外地址应为 HTTPS 根地址，不含密码、查询参数或路径。");
        var certificate = new CertificateService(database).ReadInfo();
        if (certificate is not null && (config.PublicUrl.Length == 0 || !certificate.Host.Equals(new Uri(config.PublicUrl).DnsSafeHost, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("对外域名已变化，请在 HTTPS 入口页同时导入匹配的新证书。");
        database.Write((connection, transaction) =>
        {
            if (Execute(connection, transaction, "UPDATE settings SET version=version+1,json=$json WHERE id=1 AND version=$version",
                ("$json", SerializeSettings(config)), ("$version", version)) != 1)
                throw new InvalidOperationException("配置已被另一页面或命令修改，请重新加载页面再保存。");
            return 0;
        });
    }
    public bool HasCredential => HasValue("SELECT credential FROM upstream WHERE id=1");
    public bool HasCertificate => HasValue("SELECT protected FROM secrets WHERE name='tls'");
    private bool HasValue(string sql) { using var connection = database.Open(); return Scalar(connection, null, sql) is byte[]; }

    public string? LoadCredential()
    {
        using var connection = database.Open();
        if (Scalar(connection, null, "SELECT credential FROM upstream WHERE id=1") is not byte[] encrypted) return null;
        var bytes = WindowsSecrets.Unprotect(encrypted);
        try { return Encoding.UTF8.GetString(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public byte[]? LoadCertificate()
    {
        using var connection = database.Open();
        return Scalar(connection, null, "SELECT protected FROM secrets WHERE name='tls'") is byte[] encrypted ? WindowsSecrets.Unprotect(encrypted) : null;
    }
    public void SaveCredential(string credential)
    {
        var upstreams = new UpstreamService(database); var active = upstreams.ReadActive();
        upstreams.SaveCandidate(active.Version, active.Options, credential);
    }
    public void ConfigureLegacyTls(string host, int publicPort, int listenPort)
    {
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown || host.Contains(':') || publicPort is < 1 or > 65535 || listenPort is < 1024 or > 65535)
            throw new InvalidOperationException("请输入有效域名或 IPv4 地址，端口必须有效。");
        new CertificateService(database).CreatePrivate(Read().Version, $"https://{host}:{publicPort}", listenPort);
    }
}
