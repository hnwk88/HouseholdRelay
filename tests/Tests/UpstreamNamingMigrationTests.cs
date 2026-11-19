using System.Text;
using Application.Interfaces.Security;
using Microsoft.Data.Sqlite;
using static Tests.TestSuite;

namespace Tests;

/// <summary>v3 → v4：候选名称回填、活动来源找回、候选代理改成全局一份；中途失败整体回滚。起点是真实 v3 旧库夹具，只在旧表上写数据行、不改结构。</summary>
static class UpstreamNamingMigrationTests
{
    /// <summary>
    /// 验证带代理的 v3 库：在真实 v3 旧库里换上一个已激活且带代理的旧候选（与活动行原文一致）和一个较新的无代理候选；
    /// 在提交前注入故障时升级失败，版本仍是 3、没有名称列、设置未变、旧状态仍是 activated；
    /// 去掉故障后一路升到最新版：全局代理为手动并沿用旧地址，名称按主机回填，activated 改回 passed，活动上游指回原候选（旧编号留在对外编号里），任何选项 JSON 都不再含 proxyUrl。
    /// </summary>
    public static Task ProxyMigrationAndRollback()
    {
        using var home = DatabaseUpgradeTests.Legacy("v3");
        var secret = new TestProcess(home.Path).Get<ISecretProtector>().Protect(Encoding.UTF8.GetBytes("fixture-migrated-secret"));
        const string old = """{"mode":"api-key","authentication":"bearer","baseUrl":"https://aitoll.net/v1/cc","proxyUrl":"http://127.0.0.1:7897","model":"claude-opus-5-5","diagnostic":"claude-code"}""";
        const string newer = """{"mode":"api-key","authentication":"x-api-key","baseUrl":"https://api.sftoken.top","model":"claude-opus-5-5","diagnostic":"messages"}""";
        const string report = """{"checkedAt":"2026-10-28T14:03:14Z","connection":"passed","authentication":"passed","modelDiscovery":"passed","call":"passed","models":[],"verifiedModel":"claude-opus-5-5","message":"ok"}""";
        Seed(home, connection =>
        {
            Insert(connection, "old-candidate", old, "activated", report, secret, 1);
            Insert(connection, "newer-candidate", newer, "passed", report, secret, 2);
            RelayDatabase.Execute(connection, null, "UPDATE upstream SET version=2,options_json=$json,report_json=$report,credential=$secret WHERE id=1",
                ("$json", old), ("$report", report), ("$secret", secret));
        });
        var settingsBefore = Settings(home);
        var failed = false;
        try { _ = new TestProcess(home.Path, migrationCheckpoint: () => throw new IOException("injected before v4 commit")).Database; }
        // 注入的提交前故障预期以 IOException 冒出；随后核对 v3 结构、设置和候选状态原样未动，证明整体回滚。
        catch (IOException) { failed = true; }
        Check(failed, "The injected failure must abort the upgrade");
        using (var connection = DatabaseUpgradeTests.Connect(DatabaseUpgradeTests.Database(home.Path)))
        {
            Check(Convert.ToInt32(RelayDatabase.Scalar(connection, null, "PRAGMA user_version")) == 3);
            Check(Convert.ToInt32(RelayDatabase.Scalar(connection, null, "SELECT COUNT(*) FROM pragma_table_info('upstream_candidates') WHERE name='name'")) == 0);
            Check((string?)RelayDatabase.Scalar(connection, null, "SELECT state FROM upstream_candidates WHERE id='old-candidate'") == "activated");
        }
        Check(Settings(home) == settingsBefore);
        var upgraded = new TestProcess(home.Path); var settings = upgraded.Settings.Read().Value;
        Check(settings.ProxyMode == "manual" && settings.ProxyUrl == "http://127.0.0.1:7897");
        var saved = upgraded.Upstreams.ListCandidates();
        Check(saved.Select(item => (item.Uid, item.Name, item.State)).SequenceEqual([("old-candidate", "aitoll.net", "passed"), ("newer-candidate", "api.sftoken.top", "passed")]));
        var active = upgraded.Upstreams.ReadActive();
        Check(active.CandidateId == saved[0].Id && active.Name == "aitoll.net" && active.Credential == "fixture-migrated-secret" && active.Options.Diagnostic == "claude-code");
        using (var connection = upgraded.Database.Open())
        {
            Check(Convert.ToInt32(RelayDatabase.Scalar(connection, null, "PRAGMA user_version")) == RelayDatabase.CurrentVersion);
            Check(Convert.ToInt32(RelayDatabase.Scalar(connection, null, "SELECT COUNT(*) FROM upstream_candidates WHERE options_json LIKE '%proxyUrl%'")) == 0);
            Check(!((string)RelayDatabase.Scalar(connection, null, "SELECT options_json FROM upstream WHERE id=1")!).Contains("proxyUrl"));
        }
        upgraded.Upstreams.Activate(saved[1].Id, upgraded.Upstreams.ReadActive().Version); upgraded.Upstreams.Activate(saved[0].Id, upgraded.Upstreams.ReadActive().Version);
        Check(upgraded.Upstreams.ReadActive().CandidateId == saved[0].Id, "Migrated candidates can be switched back and forth");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 验证没有代理的 v3 库：活动上游有凭据却对不上任何候选（旧版迁移来的凭据）时升级为它补建一条按主机命名的候选并关联上；
    /// 全局代理保持不使用，设置版本号不变。
    /// </summary>
    public static Task OrphanActiveWithoutProxy()
    {
        using var home = DatabaseUpgradeTests.Legacy("v3");
        var secret = new TestProcess(home.Path).Get<ISecretProtector>().Protect(Encoding.UTF8.GetBytes("fixture-orphan-secret"));
        Seed(home, connection => RelayDatabase.Execute(connection, null, "UPDATE upstream SET options_json=$json,credential=$secret WHERE id=1",
            ("$json", """{"mode":"oauth","authentication":"bearer","baseUrl":"https://api.anthropic.com","oAuthBeta":true}"""), ("$secret", secret)));
        var version = Settings(home).Split(':')[0];
        var upgraded = new TestProcess(home.Path); var active = upgraded.Upstreams.ReadActive();
        // 迁移只核对原有加密凭据；未准备官方身份的测试进程不通过活动读取开放转发。
        Check(active.CandidateId is not null && active.Name == "api.anthropic.com" && upgraded.ActiveCredential() == "fixture-orphan-secret");
        var created = upgraded.Upstreams.ReadCandidate(active.CandidateId!.Value);
        Check(created.State == "untested" && created.Options.Mode == "oauth" && created.Uid.Length == 32 && upgraded.Upstreams.ListCandidates().Count == 1);
        Check(upgraded.Settings.Read().Value.ProxyMode == "none" && upgraded.Settings.Read().Version.ToString(CultureInfo.InvariantCulture) == version);
        return Task.CompletedTask;
    }

    /// <summary>在真实 v3 旧库上清掉夹具自带的三家候选（它们带的代理会参与全局代理的选择），再交给 <paramref name="seed"/> 写入用例要的旧数据；只写数据行，不改结构。</summary>
    private static void Seed(TestHome home, Action<SqliteConnection> seed)
    {
        using var connection = DatabaseUpgradeTests.Connect(DatabaseUpgradeTests.Database(home.Path));
        Check(Convert.ToInt32(RelayDatabase.Scalar(connection, null, "PRAGMA user_version")) == 3);
        RelayDatabase.Execute(connection, null, "DELETE FROM upstream_candidates");
        seed(connection);
    }

    /// <summary>按 v3 的列顺序写入一条候选。</summary>
    private static void Insert(SqliteConnection connection, string id, string json, string state, string report, byte[] secret, long created) =>
        RelayDatabase.Execute(connection, null, "INSERT INTO upstream_candidates VALUES($id,1,$json,$secret,$state,$report,$created,NULL)",
            ("$id", id), ("$json", json), ("$secret", secret), ("$state", state), ("$report", report), ("$created", created));

    /// <summary>不经程序读出设置表的版本与原文，用来比对回滚后是否原样。</summary>
    private static string Settings(TestHome home)
    {
        using var connection = DatabaseUpgradeTests.Connect(DatabaseUpgradeTests.Database(home.Path));
        return RelayDatabase.Scalar(connection, null, "SELECT version FROM settings WHERE id=1") + ":" + RelayDatabase.Scalar(connection, null, "SELECT json FROM settings WHERE id=1");
    }
}
