using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static HouseholdRelay.RelayDatabase;

namespace HouseholdRelay;

/// <summary>网页和 CLI 共用的候选保存、诊断和原子激活服务。</summary>
public sealed class UpstreamService(RelayDatabase database)
{
    public UpstreamSnapshot ReadActive()
    {
        using var connection = database.Open();
        using var command = Command(connection, null, "SELECT version,options_json,credential,report_json FROM upstream WHERE id=1");
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("上游配置缺失。");
        return new(reader.GetInt64(0), Parse<UpstreamOptions>(reader.GetString(1)), reader.IsDBNull(2) ? null : Decrypt((byte[])reader[2]),
            reader.IsDBNull(3) ? null : Parse<UpstreamReport>(reader.GetString(3)));
    }

    public UpstreamCandidate? LatestCandidate()
    {
        using var connection = database.Open();
        var id = Scalar(connection, null, "SELECT id FROM upstream_candidates ORDER BY created_at DESC,rowid DESC LIMIT 1") as string;
        return id is null ? null : ReadCandidate(id);
    }

    public UpstreamCandidate ReadCandidate(string id)
    {
        using var connection = database.Open();
        using var command = Command(connection, null, "SELECT expected_version,options_json,state,report_json FROM upstream_candidates WHERE id=$id", ("$id", id));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("候选配置不存在，请重新加载页面。");
        return new(id, reader.GetInt64(0), Parse<UpstreamOptions>(reader.GetString(1)), reader.GetString(2),
            reader.IsDBNull(3) ? null : Parse<UpstreamReport>(reader.GetString(3)));
    }

    public string SaveCandidate(long expectedVersion, UpstreamOptions options, string credential)
    {
        options = options.Validated();
        if (credential.Length is < 1 or > 8192 || credential.Any(char.IsControl) || credential.Any(char.IsWhiteSpace))
            throw new InvalidOperationException("请填写有效的上游凭据，不能含空白或控制字符。");
        var json = JsonSerializer.Serialize(options, ConfigurationFiles.Json);
        if (credential.Length >= 8 && (json.Contains(credential, StringComparison.Ordinal) || options.BaseUrl.Contains(Uri.EscapeDataString(credential), StringComparison.Ordinal)))
            throw new InvalidOperationException("凭据只能填写在专用秘密字段，不能写入地址、代理或模型名称。");
        var plain = Encoding.UTF8.GetBytes(credential);
        try
        {
            var encrypted = WindowsSecrets.Protect(plain); var id = Guid.NewGuid().ToString("N");
            database.Write((connection, transaction) =>
            {
                if (Convert.ToInt64(Scalar(connection, transaction, "SELECT version FROM upstream WHERE id=1")) != expectedVersion)
                    throw new InvalidOperationException("活动配置已改变，请重新加载后保存候选。");
                return Execute(connection, transaction, """
                    INSERT INTO upstream_candidates VALUES($id,$version,$json,$secret,'untested',NULL,$now,NULL)
                    """, ("$id", id), ("$version", expectedVersion), ("$json", json), ("$secret", encrypted), ("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            });
            return id;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public async Task<UpstreamReport> TestCandidate(string id, bool confirmed, CancellationToken cancellation)
    {
        if (!confirmed) throw new InvalidOperationException("请先确认最小模型测试可能消耗少量上游额度。");
        var candidate = ReadCandidate(id);
        if (candidate.Options.Model.Length == 0) throw new InvalidOperationException("请填写要实际调用的模型名称，再保存候选。");
        var secret = database.Write((connection, transaction) =>
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (Execute(connection, transaction, """
                UPDATE upstream_candidates SET state='testing',report_json=NULL,test_started_at=$now
                WHERE id=$id AND (state!='testing' OR test_started_at<$expired)
                """, ("$id", id), ("$now", now), ("$expired", now - 120)) != 1)
                throw new InvalidOperationException("此候选正在检测，请稍后查看结果。");
            return (byte[])Scalar(connection, transaction, "SELECT credential FROM upstream_candidates WHERE id=$id", ("$id", id))!;
        });
        // 网络等待不占用写事务，也不接触当前服务的暂停和冷却状态。
        var report = await UpstreamProbe.Run(new(candidate.ExpectedVersion, candidate.Options, Decrypt(secret)), cancellation);
        database.Write((connection, transaction) => Execute(connection, transaction,
            "UPDATE upstream_candidates SET state=$state,report_json=$json WHERE id=$id",
            ("$state", report.CanActivate ? "passed" : "failed"), ("$json", JsonSerializer.Serialize(report, ConfigurationFiles.Json)), ("$id", id)));
        return report;
    }

    public void Activate(string id)
    {
        database.Write((connection, transaction) =>
        {
            using var command = Command(connection, transaction, "SELECT expected_version,options_json,credential,report_json,state FROM upstream_candidates WHERE id=$id", ("$id", id));
            long version; string json, reportJson; byte[] credential;
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read() || reader.GetString(4) != "passed" || reader.IsDBNull(3)) throw new InvalidOperationException("候选尚未通过最小模型调用检测。");
                version = reader.GetInt64(0); json = reader.GetString(1); credential = (byte[])reader[2]; reportJson = reader.GetString(3);
            }
            if (!Parse<UpstreamReport>(reportJson).CanActivate) throw new InvalidOperationException("候选检测未成功，不能激活。");
            var options = Parse<UpstreamOptions>(json);
            if (Execute(connection, transaction, """
                UPDATE upstream SET version=version+1,mode=$mode,base_url=$url,credential=$secret,options_json=$json,report_json=$report
                WHERE id=1 AND version=$version
                """, ("$mode", options.Mode), ("$url", options.BaseUrl), ("$secret", credential), ("$json", json), ("$report", reportJson), ("$version", version)) != 1)
                throw new InvalidOperationException("活动版本已被另一页面或命令更新，请重新保存并测试候选。");
            return Execute(connection, transaction, "UPDATE upstream_candidates SET state='activated' WHERE id=$id", ("$id", id));
        });
    }

    private static T Parse<T>(string json) => JsonSerializer.Deserialize<T>(json, ConfigurationFiles.Json) ?? throw new InvalidOperationException("上游配置损坏。");
    private static string Decrypt(byte[] encrypted)
    {
        var plain = WindowsSecrets.Unprotect(encrypted);
        try { return Encoding.UTF8.GetString(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}
