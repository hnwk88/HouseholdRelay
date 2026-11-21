using System.Text;
using Application.Interfaces.Security;
using HouseholdRelay;
using Microsoft.Data.Sqlite;
using static Tests.TestSuite;

namespace Tests;

/// <summary>保留基础库真实身份和凭据，在升级中途制造 DDL 冲突以验证完整回滚。</summary>
static class UpstreamMigrationTests
{
    /// <summary>
    /// 验证上游表结构升级（v1 → v2）的回滚：程序新建库并导入旧版文件后在第 2 版提交前停下，得到真实的版本 1 库；在上面写入管理员并预置冲突的 upstream_candidates 表，
    /// 启动时升级必须失败，且版本号、upstream 表列和上游凭据都保持原样；去掉冲突后一路升到最新版，旧凭据迁为 OAuth 模式、无诊断报告，
    /// 管理员初始化状态不变，成员按归档里的旧编号对得上新主键，停用的旧令牌仍被拒；再次启动不改变上游版本。
    /// </summary>
    public static async Task UpgradeRollback()
    {
        using var home = new TestHome(); home.WriteLegacy();
        var calls = 0;
        // 第 1 个检查点是旧文件导入，第 2 个是 v1 → v2：在第 2 个停下，导入已提交、库停在版本 1。
        try { _ = new TestProcess(home.Path, migrationCheckpoint: () => { if (++calls == 2) throw new IOException("stop-at-v1"); }).Database; }
        // 按设计在 v2 提交前停下，只接住这一个注入的异常；下面核对库版本正好是 1。
        catch (IOException error) when (error.Message == "stop-at-v1") { }
        var tools = new TestProcess(home.Path);
        var path = DatabaseUpgradeTests.Database(home.Path); string originalId;
        using (var connection = DatabaseUpgradeTests.Connect(path))
        {
            Check(Convert.ToInt32(RelayDatabase.Scalar(connection, null, "PRAGMA user_version")) == 1);
            originalId = (string)RelayDatabase.Scalar(connection, null, "SELECT id FROM users WHERE name='study'")!;
            RelayDatabase.Execute(connection, null, "INSERT INTO administrators VALUES('fixture-administrator',$hash); DELETE FROM bootstrap; CREATE TABLE upstream_candidates (unexpected TEXT);",
                ("$hash", tools.Get<IAdministratorPasswordHasher>().Hash(WebFixture.Password)));
        }
        var rejected = false;
        try { _ = new TestProcess(home.Path).Database; } catch (SqliteException) { /* 冲突的表结构让升级中途失败，预期以 SQLite 异常整体回滚；随后核对旧结构和凭据未被改动。 */ rejected = true; }
        Check(rejected, "A conflicting schema must fail the upgrade");
        using (var connection = DatabaseUpgradeTests.Connect(path))
        {
            Check(Convert.ToInt32(RelayDatabase.Scalar(connection, null, "PRAGMA user_version")) == 1);
            Check(Convert.ToInt32(RelayDatabase.Scalar(connection, null, "SELECT COUNT(*) FROM pragma_table_info('upstream') WHERE name='options_json'")) == 0);
            var credential = (byte[])RelayDatabase.Scalar(connection, null, "SELECT credential FROM upstream WHERE id=1")!;
            Check(Encoding.UTF8.GetString(tools.Get<ISecretProtector>().Unprotect(credential)) == "fixture-legacy-upstream-secret");
            RelayDatabase.Execute(connection, null, "DROP TABLE upstream_candidates");
        }
        var upgraded = new TestProcess(home.Path); var upstream = await upgraded.Upstreams.ReadActive();
        // 纯迁移不准备官方目录或触发续期；直接检查已保存密文解密后仍是旧夹具凭据。
        Check(upstream.Options.Mode == Core.Upstream.UpstreamMode.OAuth && upgraded.ActiveCredential() == "fixture-legacy-upstream-secret" && upstream.Report is null);
        Check(await upgraded.Administrators.IsInitialized() && await upgraded.Administrators.Login(WebFixture.Password) is not null);
        using (var connection = upgraded.Database.Open())
        {
            var mapped = Convert.ToInt64(RelayDatabase.Scalar(connection, null,
                "SELECT json_extract(row_json,'$.new_id') FROM migration_archive WHERE source_table='users' AND reason='replaced-id' AND json_extract(row_json,'$.id')=$old", ("$old", originalId)));
            Check((await upgraded.Identities.Clients.Authenticate("legacy-study-token"))!.UserId == mapped, "The imported member keeps its identity through the key change");
        }
        Check(await upgraded.Identities.Clients.Authenticate("legacy-office-token") is null);
        var repeated = new TestProcess(home.Path);
        Check((await repeated.Upstreams.ReadActive()).Version == upstream.Version);
    }
}
