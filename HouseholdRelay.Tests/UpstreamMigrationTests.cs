using HouseholdRelay;
using Microsoft.Data.Sqlite;
using static Tests;

/// <summary>保留基础库真实身份和凭据，在升级中途制造 DDL 冲突以验证完整回滚。</summary>
static class UpstreamMigrationTests
{
    public static Task UpgradeRollback()
    {
        using var home = new TestHome(); home.WriteLegacy(); var database = new RelayDatabase(home.Path);
        var administrators = new AdministratorService(database); administrators.Initialize(administrators.BootstrapCode()!, WebFixture.Password);
        var originalId = new IdentityService(database).Clients.Authenticate("legacy-study-token")!.Id;
        // 构造前一版的实际表结构；身份、哈希、实例标记和加密机密全部保留。
        database.Write((connection, transaction) => RelayDatabase.Execute(connection, transaction, """
            DROP TABLE upstream_candidates;
            DROP TABLE usage_requests;
            DROP TABLE usage_prices;
            ALTER TABLE upstream DROP COLUMN options_json;
            ALTER TABLE upstream DROP COLUMN report_json;
            PRAGMA user_version=1;
            CREATE TABLE upstream_candidates (unexpected TEXT);
            """));
        var rejected = false;
        try { _ = new RelayDatabase(home.Path); } catch (SqliteException) { rejected = true; }
        Check(rejected, "A conflicting schema must fail the upgrade");
        using (var connection = database.Open())
        {
            Check(Convert.ToInt32(RelayDatabase.Scalar(connection, null, "PRAGMA user_version")) == 1);
            Check(Convert.ToInt32(RelayDatabase.Scalar(connection, null, "SELECT COUNT(*) FROM pragma_table_info('upstream') WHERE name='options_json'")) == 0);
            Check(new ConfigurationService(database).LoadCredential() == "fixture-legacy-upstream-secret");
        }
        database.Write((connection, transaction) => RelayDatabase.Execute(connection, transaction, "DROP TABLE upstream_candidates"));
        var upgraded = new RelayDatabase(home.Path); var upstream = new UpstreamService(upgraded).ReadActive();
        Check(upstream.Options.Mode == "oauth" && upstream.Credential == "fixture-legacy-upstream-secret" && upstream.Report is null);
        Check(new AdministratorService(upgraded).IsInitialized);
        Check(new IdentityService(upgraded).Clients.Authenticate("legacy-study-token")!.Id == originalId);
        Check(new IdentityService(upgraded).Clients.Authenticate("legacy-office-token") is null);
        var repeated = new RelayDatabase(home.Path);
        Check(new UpstreamService(repeated).ReadActive().Version == upstream.Version);
        return Task.CompletedTask;
    }
}
