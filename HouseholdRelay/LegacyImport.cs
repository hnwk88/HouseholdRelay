using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static HouseholdRelay.RelayDatabase;

namespace HouseholdRelay;

/// <summary>仅迁移本项目的数据目录，保留原文件与导入前快照以便独立恢复。</summary>
internal static class LegacyImport
{
    internal static void Run(RelayDatabase database, Action? checkpoint)
    {
        using var connection = database.Open();
        if ((string?)Scalar(connection, null, "SELECT value FROM metadata WHERE key='legacy_imported'") == "1") return;
        var files = new Dictionary<string, byte[]>();
        foreach (var name in new[] { "server.json", "account.dpapi", "tls.dpapi", "relay-ca.pem" })
        {
            var path = System.IO.Path.Combine(database.State, name);
            if (File.Exists(path)) files.Add(name, File.ReadAllBytes(path));
        }
        if (files.Count != 0 && !files.ContainsKey("server.json"))
            throw new InvalidOperationException("旧数据目录缺少 server.json，不能推测账户和身份，请先恢复完整备份。");
        if (files.Count > 0)
        {
            var backup = System.IO.Path.Combine(database.State, "legacy-backups", DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N"));
            WindowsSecrets.ProtectDirectory(backup);
            foreach (var (name, content) in files) File.WriteAllBytes(System.IO.Path.Combine(backup, name), content);
        }
        // 备份结束才开始写事务；任何字段、解密或注入故障都会回滚全部导入。
        using var transaction = connection.BeginTransaction();
        if (files.TryGetValue("server.json", out var json))
        {
            var config = JsonSerializer.Deserialize<RelayConfiguration>(Encoding.UTF8.GetString(json).TrimStart('\uFEFF'), ConfigurationFiles.Json)
                ?? throw new InvalidOperationException("旧配置为空。");
            config.Validate();
            Execute(connection, transaction, "UPDATE settings SET json=$json,version=version+1 WHERE id=1", ("$json", SerializeSettings(config)));
            foreach (var client in config.Clients)
            {
                var userId = Guid.NewGuid().ToString("N");
                Execute(connection, transaction, "INSERT INTO users VALUES($id,$name,$name,$enabled)",
                    ("$id", userId), ("$name", client.Name), ("$enabled", client.Enabled));
                Execute(connection, transaction, "INSERT INTO tokens VALUES($id,$user,1,$hash,NULL)",
                    ("$id", Guid.NewGuid().ToString("N")), ("$user", userId), ("$hash", client.TokenHash.ToLowerInvariant()));
            }
            byte[]? credential = null;
            if (files.TryGetValue("account.dpapi", out var account))
            {
                var plain = WindowsSecrets.Unprotect(account);
                try
                {
                    var token = Encoding.UTF8.GetString(plain);
                    if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsControl)) throw new InvalidOperationException("旧上游凭据格式不正确。");
                    credential = account;
                }
                finally { CryptographicOperations.ZeroMemory(plain); }
            }
            Execute(connection, transaction, "UPDATE upstream SET mode=$mode,credential=$credential WHERE id=1", ("$mode", config.AuthMode), ("$credential", credential));
            if (files.TryGetValue("tls.dpapi", out var tls))
            {
                var plain = WindowsSecrets.Unprotect(tls);
                try
                {
                    using var certificate = Management.LoadServerCertificate(plain);
                    if (!certificate.HasPrivateKey) throw new InvalidOperationException("旧证书没有私钥。");
                }
                finally { CryptographicOperations.ZeroMemory(plain); }
                Execute(connection, transaction, "INSERT INTO secrets VALUES('tls',$value)", ("$value", tls));
            }
            if (files.TryGetValue("relay-ca.pem", out var ca))
                Execute(connection, transaction, "INSERT INTO public_assets VALUES('legacy_ca',$value)", ("$value", Encoding.UTF8.GetString(ca)));
        }
        checkpoint?.Invoke();
        Execute(connection, transaction, "UPDATE metadata SET value='1' WHERE key='legacy_imported'");
        transaction.Commit();
    }
}
