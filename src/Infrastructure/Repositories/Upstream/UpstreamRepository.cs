using System.Text.Json;
using Application.Contracts.Upstream;
using Application.Interfaces.Upstream;
using Core.Configuration;
using Core.Upstream;
using Infrastructure.Persistence;
using static Infrastructure.Persistence.RelayDatabase;

namespace Infrastructure.Repositories.Upstream;

/// <summary>upstream 与 upstream_candidates 表的读写；所有版本检查都在写事务内完成。</summary>
internal sealed class UpstreamRepository(RelayDatabase database) : IUpstreamRepository
{
    /// <summary>读取活动上游原始行。</summary>
    public StoredUpstream? ReadActive()
    {
        using var connection = database.Open();
        using var command = Command(connection, null, "SELECT version,options_json,credential,report_json FROM upstream WHERE id=1");
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new(reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : (byte[])reader[2], reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    /// <summary>读取活动上游的加密凭据。</summary>
    public byte[]? ReadActiveCredential()
    { using var connection = database.Open(); return Scalar(connection, null, "SELECT credential FROM upstream WHERE id=1") as byte[]; }

    /// <summary>最近创建的候选主键。</summary>
    public string? LatestCandidateId()
    {
        using var connection = database.Open();
        return Scalar(connection, null, "SELECT id FROM upstream_candidates ORDER BY created_at DESC,rowid DESC LIMIT 1") as string;
    }

    /// <summary>按主键读取候选的非秘密部分。</summary>
    public StoredCandidate? ReadCandidate(string id)
    {
        using var connection = database.Open();
        using var command = Command(connection, null, "SELECT expected_version,options_json,state,report_json FROM upstream_candidates WHERE id=$id", ("$id", id));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    /// <summary>活动版本匹配时写入未测试候选。</summary>
    public void InsertCandidate(string id, long expectedVersion, string optionsJson, byte[] protectedCredential) => database.Write((connection, transaction) =>
    {
        if (Convert.ToInt64(Scalar(connection, transaction, "SELECT version FROM upstream WHERE id=1")) != expectedVersion)
            throw new InvalidOperationException("活动配置已改变，请重新加载后保存候选。");
        return Execute(connection, transaction, """
            INSERT INTO upstream_candidates VALUES($id,$version,$json,$secret,'untested',NULL,$now,NULL)
            """, ("$id", id), ("$version", expectedVersion), ("$json", optionsJson), ("$secret", protectedCredential), ("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
    });

    /// <summary>标记候选检测中并取出加密凭据；两分钟内的并发检测被拒绝。</summary>
    public byte[] BeginCandidateTest(string id) => database.Write((connection, transaction) =>
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (Execute(connection, transaction, """
            UPDATE upstream_candidates SET state='testing',report_json=NULL,test_started_at=$now
            WHERE id=$id AND (state!='testing' OR test_started_at<$expired)
            """, ("$id", id), ("$now", now), ("$expired", now - 120)) != 1)
            throw new InvalidOperationException("此候选正在检测，请稍后查看结果。");
        return (byte[])Scalar(connection, transaction, "SELECT credential FROM upstream_candidates WHERE id=$id", ("$id", id))!;
    });

    /// <summary>保存检测结果。</summary>
    public void FinishCandidateTest(string id, string state, string reportJson) => database.Write((connection, transaction) => Execute(connection, transaction,
        "UPDATE upstream_candidates SET state=$state,report_json=$json WHERE id=$id", ("$state", state), ("$json", reportJson), ("$id", id)));

    /// <summary>核对检测结果与活动版本后写为新的活动版本。</summary>
    public void Activate(string id) => database.Write((connection, transaction) =>
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

    /// <summary>解析库中的上游 JSON；为空时视为配置损坏。</summary>
    private static T Parse<T>(string json) => JsonSerializer.Deserialize<T>(json, ConfigurationFiles.Json) ?? throw new InvalidOperationException("上游配置损坏。");
}
