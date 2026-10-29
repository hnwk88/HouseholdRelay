using HouseholdRelay;
using Microsoft.Data.Sqlite;
using static Tests.TestSuite;

namespace Tests;

/// <summary>保留基础库真实身份和凭据，在升级中途制造 DDL 冲突以验证完整回滚。</summary>
static class UpstreamMigrationTests
{
    /// <summary>
    /// 验证上游表结构升级的回滚：把库退回到前一版结构并预置冲突的 upstream_candidates 表，启动时升级必须失败，
    /// 且版本号、upstream 表列和上游凭据都保持原样；去掉冲突后升级成功，旧凭据迁为 OAuth 模式、无诊断报告，
    /// 管理员初始化状态和成员编号不变，停用的旧令牌仍被拒；再次启动不改变上游版本。
    /// </summary>
    public static Task UpgradeRollback()
    {
        using var home = new TestHome(); home.WriteLegacy(); var process = new TestProcess(home.Path); var database = process.Database;
        var administrators = process.Administrators; administrators.Initialize(administrators.BootstrapCode()!, WebFixture.Password);
        var originalId = process.Identities.Clients.Authenticate("legacy-study-token")!.Id;
        // 构造前一版的实际表结构；身份、哈希、实例标记和加密机密全部保留。
        database.Write((connection, transaction) => RelayDatabase.Execute(connection, transaction, """
            DROP TABLE upstream_candidates;
            DROP TABLE usage_requests;
            DROP TABLE usage_prices;
            ALTER TABLE upstream DROP COLUMN options_json;
            ALTER TABLE upstream DROP COLUMN report_json;
            ALTER TABLE upstream DROP COLUMN candidate_id;
            PRAGMA user_version=1;
            CREATE TABLE upstream_candidates (unexpected TEXT);
            """));
        var rejected = false;
        try { _ = new TestProcess(home.Path).Database; } catch (SqliteException) { /* 冲突的表结构让升级中途失败，预期以 SQLite 异常整体回滚；随后核对旧结构和凭据未被改动。 */ rejected = true; }
        Check(rejected, "A conflicting schema must fail the upgrade");
        using (var connection = database.Open())
        {
            Check(Convert.ToInt32(RelayDatabase.Scalar(connection, null, "PRAGMA user_version")) == 1);
            Check(Convert.ToInt32(RelayDatabase.Scalar(connection, null, "SELECT COUNT(*) FROM pragma_table_info('upstream') WHERE name='options_json'")) == 0);
            Check(process.Settings.LoadCredential() == "fixture-legacy-upstream-secret");
        }
        database.Write((connection, transaction) => RelayDatabase.Execute(connection, transaction, "DROP TABLE upstream_candidates"));
        var upgraded = new TestProcess(home.Path); var upstream = upgraded.Upstreams.ReadActive();
        Check(upstream.Options.Mode == "oauth" && upstream.Credential == "fixture-legacy-upstream-secret" && upstream.Report is null);
        Check(upgraded.Administrators.IsInitialized);
        Check(upgraded.Identities.Clients.Authenticate("legacy-study-token")!.Id == originalId);
        Check(upgraded.Identities.Clients.Authenticate("legacy-office-token") is null);
        var repeated = new TestProcess(home.Path);
        Check(repeated.Upstreams.ReadActive().Version == upstream.Version);
        return Task.CompletedTask;
    }
}
