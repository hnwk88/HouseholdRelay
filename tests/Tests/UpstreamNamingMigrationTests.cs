using System.Text;
using Application.Interfaces.Security;
using Microsoft.Data.Sqlite;
using static Tests.TestSuite;

namespace Tests;

/// <summary>v3 → v4：候选名称回填、活动来源找回、候选代理改成全局一份；中途失败整体回滚。</summary>
static class UpstreamNamingMigrationTests
{
    /// <summary>
    /// 验证带代理的 v3 库：先把新库退回 v3 结构，放入一个已激活且带代理的旧候选（与活动行原文一致）和一个较新的无代理候选；
    /// 在提交前注入故障时升级失败，版本仍是 3、没有名称列、设置未变、旧状态仍是 activated；
    /// 去掉故障后升级成功：全局代理为手动并沿用旧地址，名称按主机回填，activated 改回 passed，活动上游指回原候选，任何选项 JSON 都不再含 proxyUrl。
    /// </summary>
    public static Task ProxyMigrationAndRollback()
    {
        using var home = new TestHome(); var process = new TestProcess(home.Path); var database = process.Database;
        var secret = process.Get<ISecretProtector>().Protect(Encoding.UTF8.GetBytes("fixture-migrated-secret"));
        const string old = """{"mode":"api-key","authentication":"bearer","baseUrl":"https://aitoll.net/v1/cc","proxyUrl":"http://127.0.0.1:7897","model":"claude-opus-5-5","diagnostic":"claude-code"}""";
        const string newer = """{"mode":"api-key","authentication":"x-api-key","baseUrl":"https://api.sftoken.top","model":"claude-opus-5-5","diagnostic":"messages"}""";
        const string report = """{"checkedAt":"2026-10-28T14:03:14Z","connection":"passed","authentication":"passed","modelDiscovery":"passed","call":"passed","models":[],"verifiedModel":"claude-opus-5-5","message":"ok"}""";
        Downgrade(database, connection =>
        {
            Insert(connection, "old-candidate", old, "activated", report, secret, 1);
            Insert(connection, "newer-candidate", newer, "passed", report, secret, 2);
            RelayDatabase.Execute(connection, null, "UPDATE upstream SET version=2,options_json=$json,report_json=$report,credential=$secret WHERE id=1",
                ("$json", old), ("$report", report), ("$secret", secret));
        });
        var settingsBefore = Settings(database);
        var failed = false;
        try { _ = new TestProcess(home.Path, migrationCheckpoint: () => throw new IOException("injected before v4 commit")).Database; }
        // 注入的提交前故障预期以 IOException 冒出；随后核对 v3 结构、设置和候选状态原样未动，证明整体回滚。
        catch (IOException) { failed = true; }
        Check(failed, "The injected failure must abort the upgrade");
        using (var connection = database.Open())
        {
            Check(Convert.ToInt32(RelayDatabase.Scalar(connection, null, "PRAGMA user_version")) == 3);
            Check(Convert.ToInt32(RelayDatabase.Scalar(connection, null, "SELECT COUNT(*) FROM pragma_table_info('upstream_candidates') WHERE name='name'")) == 0);
            Check((string?)RelayDatabase.Scalar(connection, null, "SELECT state FROM upstream_candidates WHERE id='old-candidate'") == "activated");
        }
        Check(Settings(database) == settingsBefore);
        var upgraded = new TestProcess(home.Path); var settings = upgraded.Settings.Read().Value;
        Check(settings.ProxyMode == "manual" && settings.ProxyUrl == "http://127.0.0.1:7897");
        var saved = upgraded.Upstreams.ListCandidates();
        Check(saved.Select(item => (item.Id, item.Name, item.State)).SequenceEqual([("old-candidate", "aitoll.net", "passed"), ("newer-candidate", "api.sftoken.top", "passed")]));
        var active = upgraded.Upstreams.ReadActive();
        Check(active.CandidateId == "old-candidate" && active.Name == "aitoll.net" && active.Credential == "fixture-migrated-secret" && active.Options.Diagnostic == "claude-code");
        using (var connection = upgraded.Database.Open())
        {
            Check(Convert.ToInt32(RelayDatabase.Scalar(connection, null, "PRAGMA user_version")) == 4);
            Check(Convert.ToInt32(RelayDatabase.Scalar(connection, null, "SELECT COUNT(*) FROM upstream_candidates WHERE options_json LIKE '%proxyUrl%'")) == 0);
            Check(!((string)RelayDatabase.Scalar(connection, null, "SELECT options_json FROM upstream WHERE id=1")!).Contains("proxyUrl"));
        }
        upgraded.Upstreams.Activate("newer-candidate", upgraded.Upstreams.ReadActive().Version); upgraded.Upstreams.Activate("old-candidate", upgraded.Upstreams.ReadActive().Version);
        Check(upgraded.Upstreams.ReadActive().CandidateId == "old-candidate", "Migrated candidates can be switched back and forth");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 验证没有代理的 v3 库：活动上游有凭据却对不上任何候选（旧版迁移来的凭据）时升级为它补建一条按主机命名的候选并关联上；
    /// 全局代理保持不使用，设置版本号不变。
    /// </summary>
    public static Task OrphanActiveWithoutProxy()
    {
        using var home = new TestHome(); var process = new TestProcess(home.Path); var database = process.Database;
        var secret = process.Get<ISecretProtector>().Protect(Encoding.UTF8.GetBytes("fixture-orphan-secret"));
        Downgrade(database, connection => RelayDatabase.Execute(connection, null, "UPDATE upstream SET options_json=$json,credential=$secret WHERE id=1",
            ("$json", """{"mode":"oauth","authentication":"bearer","baseUrl":"https://api.anthropic.com","oAuthBeta":true}"""), ("$secret", secret)));
        var version = process.Settings.Read().Version;
        var upgraded = new TestProcess(home.Path); var active = upgraded.Upstreams.ReadActive();
        Check(active.CandidateId is not null && active.Name == "api.anthropic.com" && active.Credential == "fixture-orphan-secret");
        var created = upgraded.Upstreams.ReadCandidate(active.CandidateId!);
        Check(created.State == "untested" && created.Options.Mode == "oauth" && upgraded.Upstreams.ListCandidates().Count == 1);
        Check(upgraded.Settings.Read().Value.ProxyMode == "none" && upgraded.Settings.Read().Version == version);
        return Task.CompletedTask;
    }

    /// <summary>把新建的 v4 库退回 v3 结构（去掉名称列与来源列），在同一事务里交给 <paramref name="seed"/> 放入旧数据。</summary>
    private static void Downgrade(RelayDatabase database, Action<SqliteConnection> seed)
    {
        using var connection = database.Open();
        RelayDatabase.Execute(connection, null, "ALTER TABLE upstream_candidates DROP COLUMN name; ALTER TABLE upstream DROP COLUMN candidate_id; PRAGMA user_version=3;");
        seed(connection);
    }

    /// <summary>按 v3 的列顺序写入一条候选。</summary>
    private static void Insert(SqliteConnection connection, string id, string json, string state, string report, byte[] secret, long created) =>
        RelayDatabase.Execute(connection, null, "INSERT INTO upstream_candidates VALUES($id,1,$json,$secret,$state,$report,$created,NULL)",
            ("$id", id), ("$json", json), ("$secret", secret), ("$state", state), ("$report", report), ("$created", created));

    /// <summary>读出设置表的原文与版本，用来比对回滚后是否原样。</summary>
    private static string Settings(RelayDatabase database)
    {
        using var connection = database.Open();
        return RelayDatabase.Scalar(connection, null, "SELECT version FROM settings WHERE id=1") + ":" + RelayDatabase.Scalar(connection, null, "SELECT json FROM settings WHERE id=1");
    }
}
