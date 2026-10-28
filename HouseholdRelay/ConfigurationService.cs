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
        if (credential.Length is < 16 or > 8192 || credential.Any(char.IsControl)) throw new InvalidOperationException("上游令牌格式不正确。");
        var plain = Encoding.UTF8.GetBytes(credential);
        try
        {
            var encrypted = WindowsSecrets.Protect(plain);
            database.Write((connection, transaction) => Execute(connection, transaction,
                "UPDATE upstream SET credential=$value,version=version+1 WHERE id=1", ("$value", encrypted)));
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public void ConfigureLegacyTls(string host, int publicPort, int listenPort)
    {
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown || host.Contains(':') || publicPort is < 1 or > 65535 || listenPort is < 1024 or > 65535)
            throw new InvalidOperationException("请输入有效域名或 IPv4 地址，端口必须有效。");
        using var certificate = Management.CreateCertificate(host, out var ca);
        var pfx = certificate.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx);
        try
        {
            var encrypted = WindowsSecrets.Protect(pfx);
            database.Write((connection, transaction) =>
            {
                var config = JsonSerializer.Deserialize<RelayConfiguration>((string)Scalar(connection, transaction, "SELECT json FROM settings WHERE id=1")!, ConfigurationFiles.Json)!;
                config.PublicUrl = $"https://{host}:{publicPort}"; config.ListenPort = listenPort; config.Validate();
                Execute(connection, transaction, "UPDATE settings SET json=$json,version=version+1 WHERE id=1", ("$json", SerializeSettings(config)));
                Execute(connection, transaction, "INSERT INTO secrets VALUES('tls',$value) ON CONFLICT(name) DO UPDATE SET protected=excluded.protected", ("$value", encrypted));
                return Execute(connection, transaction, "INSERT INTO public_assets VALUES('legacy_ca',$value) ON CONFLICT(name) DO UPDATE SET value=excluded.value", ("$value", ca));
            });
        }
        finally { CryptographicOperations.ZeroMemory(pfx); }
    }
}
