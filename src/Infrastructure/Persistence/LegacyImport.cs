using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Application.Interfaces.Certificates;
using Application.Interfaces.Security;
using Core.Configuration;
using static Infrastructure.Persistence.RelayDatabase;

namespace Infrastructure.Persistence;

/// <summary>仅迁移本项目的数据目录，保留原文件与导入前快照以便独立恢复。</summary>
internal sealed class LegacyImport(ISecretProtector protector, ICertificateFactory certificates, RelayDataOptions options) : IDatabaseMigration
{
    /// <summary>排在第一位（10）：先把旧版文件导入基础库，再交给上游和用量架构升级。</summary>
    public int Order => 10;

    /// <summary>若元数据未标记已导入，就读取旧版 server.json、account.dpapi、tls.dpapi、relay-ca.pem：先把原文件复制到受保护的 legacy-backups 快照目录，再在一个事务里导入设置、用户与令牌哈希、上游凭据、TLS 证书和旧 CA，并写入已导入标记；校验或解密失败时整体回滚，原文件始终不动。旧凭据和证书只在内存里短暂解密校验，库中仍存 DPAPI 密文。</summary>
    public void Apply(RelayDatabase database)
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
            protector.ProtectDirectory(backup);
            foreach (var (name, content) in files) File.WriteAllBytes(System.IO.Path.Combine(backup, name), content);
        }
        // 备份结束才开始写事务；任何字段、解密或注入故障都会回滚全部导入。
        using var transaction = connection.BeginTransaction();
        if (files.TryGetValue("server.json", out var json))
        {
            var config = JsonSerializer.Deserialize<RelayConfiguration>(Encoding.UTF8.GetString(json).TrimStart('﻿'), ConfigurationFiles.Json)
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
                var plain = protector.Unprotect(account);
                try
                {
                    var token = Encoding.UTF8.GetString(plain);
                    if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsControl)) throw new InvalidOperationException("旧上游凭据格式不正确。");
                    credential = account;
                }
                // 解密出的明文凭据只用于格式校验，无论校验成败都立即清零，不在内存中停留。
                finally { CryptographicOperations.ZeroMemory(plain); }
            }
            Execute(connection, transaction, "UPDATE upstream SET mode=$mode,credential=$credential WHERE id=1", ("$mode", config.AuthMode), ("$credential", credential));
            if (files.TryGetValue("tls.dpapi", out var tls))
            {
                var plain = protector.Unprotect(tls);
                try
                {
                    using var certificate = certificates.LoadServerCertificate(plain);
                    if (!certificate.HasPrivateKey) throw new InvalidOperationException("旧证书没有私钥。");
                }
                // 解密出的证书和私钥字节只用于确认含私钥，校验后立即清零；入库的仍是原 DPAPI 密文。
                finally { CryptographicOperations.ZeroMemory(plain); }
                Execute(connection, transaction, "INSERT INTO secrets VALUES('tls',$value)", ("$value", tls));
            }
            if (files.TryGetValue("relay-ca.pem", out var ca))
                Execute(connection, transaction, "INSERT INTO public_assets VALUES('legacy_ca',$value)", ("$value", Encoding.UTF8.GetString(ca)));
        }
        options.MigrationCheckpoint?.Invoke();
        Execute(connection, transaction, "UPDATE metadata SET value='1' WHERE key='legacy_imported'");
        transaction.Commit();
    }
}
