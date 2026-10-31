using System.Text.Json;
using System.Text.Json.Nodes;
using Core.Configuration;
using Core.Upstream;
using static Infrastructure.Persistence.RelayDatabase;

namespace Infrastructure.Persistence;

/// <summary>v3 → v4：已保存上游带供应商名称、活动上游记下来源主键、候选上的代理地址改成全局一份；整个升级在一个事务里，失败全部回滚。</summary>
internal sealed class UpstreamNamingSchema(RelayDataOptions options) : IDatabaseMigration
{
    /// <summary>排在最后（40）：依赖用量架构升级已把库带到版本 3。</summary>
    public int Order => 40;

    /// <summary>
    /// 已是 4 或更高时直接返回，不是 3 时拒绝。在一个事务里：候选表加 name 列并按地址主机名回填，活动表加 candidate_id 列；
    /// 旧的 activated 状态改回 passed（激活不再改变候选状态，才能切回）；活动上游按「选项与报告原文一致的已激活候选」找回来源，
    /// 找不到且已有凭据时为它补建一条候选；活动或任一候选带 proxyUrl 时全局代理设为手动并沿用（活动优先，再按最近候选），
    /// 并从所有选项 JSON 删掉 proxyUrl；没有代理时不改设置，默认就是不使用代理。最后写入版本号 4。
    /// </summary>
    public void Apply(RelayDatabase database)
    {
        using var connection = database.Open();
        var version = Convert.ToInt32(Scalar(connection, null, "PRAGMA user_version"));
        if (version is 4 or 5 or 6 or 7) return;
        if (version != 3) throw new InvalidOperationException("数据库版本不支持。");
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, """
            ALTER TABLE upstream_candidates ADD COLUMN name TEXT NOT NULL DEFAULT '';
            ALTER TABLE upstream ADD COLUMN candidate_id TEXT;
            """);
        string activeJson, activeReport; long activeVersion; byte[]? activeCredential;
        using (var command = Command(connection, transaction, "SELECT version,options_json,credential,report_json FROM upstream WHERE id=1"))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw new InvalidOperationException("上游配置缺失，请恢复备份。");
            activeVersion = reader.GetInt64(0); activeJson = reader.GetString(1);
            activeCredential = reader.IsDBNull(2) ? null : (byte[])reader[2]; activeReport = reader.IsDBNull(3) ? "" : reader.GetString(3);
        }
        var proxy = ProxyOf(activeJson);
        var candidates = new List<(string Id, string Json, string State, string Report)>();
        using (var command = Command(connection, transaction, "SELECT id,options_json,state,report_json FROM upstream_candidates ORDER BY created_at DESC,rowid DESC"))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) candidates.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? "" : reader.GetString(3)));
        string? source = null;
        foreach (var (id, json, state, report) in candidates)
        {
            proxy ??= ProxyOf(json);
            // 激活时选项与报告是原样复制过去的，原文完全一致的已激活候选就是活动上游的来源；多条时取最近的一条。
            if (source is null && state == "activated" && json == activeJson && report == activeReport) source = id;
            Execute(connection, transaction, "UPDATE upstream_candidates SET name=$name,options_json=$json WHERE id=$id",
                ("$name", HostOf(json)), ("$json", WithoutProxy(json)), ("$id", id));
        }
        Execute(connection, transaction, "UPDATE upstream_candidates SET state='passed' WHERE state='activated'");
        if (source is null && activeCredential is not null)
        {
            // 迁移来的凭据或命令行直接写入的活动上游没有候选行：补建一条，列表里才能看到它、切走后才能切回。
            source = Guid.NewGuid().ToString("N");
            var passed = activeReport.Length != 0 && JsonSerializer.Deserialize<UpstreamReport>(activeReport, ConfigurationFiles.Json)?.CanActivate == true;
            Execute(connection, transaction, """
                INSERT INTO upstream_candidates(id,expected_version,options_json,credential,state,report_json,created_at,test_started_at,name)
                VALUES($id,$version,$json,$secret,$state,$report,$now,NULL,$name)
                """, ("$id", source), ("$version", activeVersion), ("$json", WithoutProxy(activeJson)), ("$secret", activeCredential),
                ("$state", passed ? "passed" : "untested"), ("$report", activeReport.Length == 0 ? null : activeReport),
                ("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), ("$name", HostOf(activeJson)));
        }
        Execute(connection, transaction, "UPDATE upstream SET options_json=$json,candidate_id=$source WHERE id=1", ("$json", WithoutProxy(activeJson)), ("$source", source));
        if (proxy is not null)
        {
            var settings = JsonSerializer.Deserialize<RelayConfiguration>((string)Scalar(connection, transaction, "SELECT json FROM settings WHERE id=1")!, ConfigurationFiles.Json)
                ?? throw new InvalidOperationException("实例配置损坏，请恢复备份。");
            settings.ProxyMode = "manual"; settings.ProxyUrl = proxy; settings.UpstreamProxy = null;
            Execute(connection, transaction, "UPDATE settings SET json=$json,version=version+1 WHERE id=1", ("$json", SerializeSettings(settings)));
        }
        options.MigrationCheckpoint?.Invoke();
        Execute(connection, transaction, "PRAGMA user_version=4");
        transaction.Commit();
    }

    /// <summary>取出选项 JSON 里旧的 proxyUrl；为空或已不符合代理地址规则时视为没有，不让一条坏数据挡住启动。</summary>
    private static string? ProxyOf(string json)
    {
        var value = Property(JsonNode.Parse(json)?.AsObject(), "proxyUrl")?.GetValue<string?>();
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return UpstreamAddress.Proxy(value).AbsoluteUri.TrimEnd('/'); }
        // 旧值早已不合规则时按没有代理处理；转发本来就会拒绝它。
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>返回删掉 proxyUrl 后的选项 JSON；其余字段原样保留。</summary>
    private static string WithoutProxy(string json)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        foreach (var key in node.Select(item => item.Key).Where(key => key.Equals("proxyUrl", StringComparison.OrdinalIgnoreCase)).ToArray()) node.Remove(key);
        return node.ToJsonString(ConfigurationFiles.Json);
    }

    /// <summary>按地址主机名回填供应商名称；地址无法解析时用「未命名上游」。</summary>
    private static string HostOf(string json) =>
        Uri.TryCreate(Property(JsonNode.Parse(json)?.AsObject(), "baseUrl")?.GetValue<string>(), UriKind.Absolute, out var uri) ? uri.Host : "未命名上游";

    /// <summary>按名称不区分大小写取 JSON 对象的属性。</summary>
    private static JsonNode? Property(JsonObject? node, string name) => node?.FirstOrDefault(item => item.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
}
