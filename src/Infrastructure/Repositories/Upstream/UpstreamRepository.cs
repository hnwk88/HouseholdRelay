using System.Text.Json;
using Application.Contracts.Upstream;
using Application.Interfaces.Upstream;
using Core.Configuration;
using Core.Upstream;
using Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using static Infrastructure.Persistence.RelayDatabase;

namespace Infrastructure.Repositories.Upstream;

/// <summary>upstream 与 upstream_candidates 表的读写；所有版本检查都在写事务内完成，名称只做显示，关联一律走主键。</summary>
internal sealed class UpstreamRepository(RelayDatabase database) : IUpstreamRepository
{
    private const string CandidateColumns = "id,name,expected_version,options_json,state,report_json";

    /// <summary>读取活动上游原始行，并按 candidate_id 取出它来自的已保存上游的当前名称。</summary>
    public StoredUpstream? ReadActive()
    {
        using var connection = database.Open();
        using var command = Command(connection, null, """
            SELECT u.version,u.options_json,u.credential,u.report_json,u.candidate_id,c.name
            FROM upstream u LEFT JOIN upstream_candidates c ON c.id=u.candidate_id WHERE u.id=1
            """);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new(reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : (byte[])reader[2], reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5));
    }

    /// <summary>读取活动上游的加密凭据。</summary>
    public byte[]? ReadActiveCredential()
    { using var connection = database.Open(); return Scalar(connection, null, "SELECT credential FROM upstream WHERE id=1") as byte[]; }

    /// <summary>最近创建的已保存上游主键。</summary>
    public string? LatestCandidateId()
    {
        using var connection = database.Open();
        return Scalar(connection, null, "SELECT id FROM upstream_candidates ORDER BY created_at DESC,rowid DESC LIMIT 1") as string;
    }

    /// <summary>按创建先后列出全部已保存上游的非秘密部分。</summary>
    public IReadOnlyList<StoredCandidate> ListCandidates()
    {
        using var connection = database.Open();
        using var command = Command(connection, null, $"SELECT {CandidateColumns} FROM upstream_candidates ORDER BY created_at,rowid");
        using var reader = command.ExecuteReader();
        var rows = new List<StoredCandidate>();
        while (reader.Read()) rows.Add(Candidate(reader));
        return rows;
    }

    /// <summary>按主键读取已保存上游的非秘密部分。</summary>
    public StoredCandidate? ReadCandidate(string id)
    {
        using var connection = database.Open();
        using var command = Command(connection, null, $"SELECT {CandidateColumns} FROM upstream_candidates WHERE id=$id", ("$id", id));
        using var reader = command.ExecuteReader();
        return reader.Read() ? Candidate(reader) : null;
    }

    /// <summary>活动版本匹配时写入带名称的未测试上游。</summary>
    public void InsertCandidate(string id, long expectedVersion, string name, string optionsJson, byte[] protectedCredential) => database.Write((connection, transaction) =>
    {
        if (Convert.ToInt64(Scalar(connection, transaction, "SELECT version FROM upstream WHERE id=1")) != expectedVersion)
            throw new InvalidOperationException("活动配置已改变，请重新加载后再保存。");
        return Execute(connection, transaction, """
            INSERT INTO upstream_candidates(id,expected_version,options_json,credential,state,report_json,created_at,test_started_at,name)
            VALUES($id,$version,$json,$secret,'untested',NULL,$now,NULL,$name)
            """, ("$id", id), ("$version", expectedVersion), ("$json", optionsJson), ("$secret", protectedCredential),
            ("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), ("$name", name));
    });

    /// <summary>只改名称列。</summary>
    public void Rename(string id, string name) => database.Write((connection, transaction) =>
    {
        if (Execute(connection, transaction, "UPDATE upstream_candidates SET name=$name WHERE id=$id", ("$name", name), ("$id", id)) != 1)
            throw new InvalidOperationException("这个上游不存在，请重新加载页面。");
        return 0;
    });

    /// <summary>写回配置与报告并置为未测试。</summary>
    public void SaveUntested(string id, string optionsJson, string? reportJson) => database.Write((connection, transaction) =>
    {
        if (Execute(connection, transaction, "UPDATE upstream_candidates SET options_json=$json,report_json=$report,state='untested' WHERE id=$id",
            ("$json", optionsJson), ("$report", reportJson), ("$id", id)) != 1)
            throw new InvalidOperationException("这个上游不存在，请重新加载页面。");
        return 0;
    });

    /// <summary>标记候选检测中并取出加密凭据；两分钟内的并发检测被拒绝。</summary>
    public byte[] BeginCandidateTest(string id) => database.Write((connection, transaction) =>
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (Execute(connection, transaction, """
            UPDATE upstream_candidates SET state='testing',report_json=NULL,test_started_at=$now
            WHERE id=$id AND (state!='testing' OR test_started_at<$expired)
            """, ("$id", id), ("$now", now), ("$expired", now - 120)) != 1)
            throw new InvalidOperationException("这个上游正在检测，请稍后刷新页面查看结果。");
        return (byte[])Scalar(connection, transaction, "SELECT credential FROM upstream_candidates WHERE id=$id", ("$id", id))!;
    });

    /// <summary>保存检测结果与实际采用的配置。</summary>
    public void FinishCandidateTest(string id, string state, string reportJson, string optionsJson) => database.Write((connection, transaction) => Execute(connection, transaction,
        "UPDATE upstream_candidates SET state=$state,report_json=$json,options_json=$options WHERE id=$id",
        ("$state", state), ("$json", reportJson), ("$options", optionsJson), ("$id", id)));

    /// <summary>核对检测结果与活动版本后写为新的活动版本并记下来源主键；候选自身的检测状态不变，之后仍可切回。</summary>
    public void Activate(string id, long? expectedVersion) => database.Write((connection, transaction) =>
    {
        using var command = Command(connection, transaction, "SELECT expected_version,options_json,credential,report_json,state FROM upstream_candidates WHERE id=$id", ("$id", id));
        long version; string json, reportJson; byte[] credential;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read() || reader.GetString(4) != "passed" || reader.IsDBNull(3)) throw new InvalidOperationException("这个上游还没有通过最小模型调用测试，不能切换。请先测试。");
            version = expectedVersion ?? reader.GetInt64(0); json = reader.GetString(1); credential = (byte[])reader[2]; reportJson = reader.GetString(3);
        }
        if (!Parse<UpstreamReport>(reportJson).CanActivate) throw new InvalidOperationException("这个上游的测试没有通过，不能切换。");
        var options = Parse<UpstreamOptions>(json);
        if (Execute(connection, transaction, """
            UPDATE upstream SET version=version+1,mode=$mode,base_url=$url,credential=$secret,options_json=$json,report_json=$report,candidate_id=$candidate
            WHERE id=1 AND version=$version
            """, ("$mode", options.Mode), ("$url", options.BaseUrl), ("$secret", credential), ("$json", json), ("$report", reportJson),
            ("$candidate", id), ("$version", version)) != 1)
            throw new InvalidOperationException("活动上游已被另一页面或命令更换，请重新加载页面后再切换。");
        return 0;
    });

    /// <summary>按 <see cref="CandidateColumns"/> 的列顺序读出一行已保存上游。</summary>
    private static StoredCandidate Candidate(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3),
        reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5));

    /// <summary>解析库中的上游 JSON；为空时视为配置损坏。</summary>
    private static T Parse<T>(string json) => JsonSerializer.Deserialize<T>(json, ConfigurationFiles.Json) ?? throw new InvalidOperationException("上游配置损坏。");
}
