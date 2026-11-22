using System.Globalization;
using Application.Interfaces.Configuration;
using Core.Upstream;
using Core.Usage;
using Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Repositories;
using static Tests.TestSuite;

namespace Tests;

/// <summary>
/// 库版本 11 与运行时存取（订单 151「数据三」）：EF 模型与实际表结构一致、连接安全在 EF 下逐项配回、并发令牌、物理删除与软删除、v10→v11 升级不改旧值且失败整体回滚，
/// 以及正式库副本升级复算命令。所有用例用自己的临时数据目录。
/// </summary>
static class EntityStorageTests
{
    /// <summary>
    /// EF 模型与库版本 11 的实际表结构逐项相等：表、列名、可空、主键、外键（列、父表、父列、是否级联）、唯一约束（列与是否带条件）、普通索引名。
    /// 再在一份副本上把 users.display_name 改名，比对必须报出这一处——证明这条用例真的在比，模型或建表语句任一边改了另一边没跟上就红。
    /// </summary>
    public static Task ModelMatchesSchema()
    {
        using var home = new TestHome(); var process = new TestProcess(home.Path); _ = process.Database;
        using var scope = process.Get<IServiceScopeFactory>().CreateScope();
        var model = scope.ServiceProvider.GetRequiredService<RelayDbContext>().GetService<IDesignTimeModel>().Model;
        using (var connection = process.Database.Open())
        {
            var differences = SchemaDifferences(model, connection);
            Check(differences.Count == 0, "EF model and version 11 schema differ: " + string.Join("；", differences));
            var copy = Path.Combine(home.Path, "mutated.db");
            using var mutated = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = copy, Pooling = false }.ToString());
            mutated.Open(); connection.BackupDatabase(mutated);
            RelayDatabase.Execute(mutated, null, "ALTER TABLE users RENAME COLUMN display_name TO shown_name");
            var broken = SchemaDifferences(model, mutated);
            Check(broken.Any(item => item.Contains("display_name", StringComparison.Ordinal)), "The comparison must report a renamed column: " + string.Join("；", broken));
        }
        return Task.CompletedTask;
    }

    /// <summary>EF 打开的连接开着外键检查。</summary>
    public static Task ForeignKeysOn() => WithContext((context, _) => Check(Convert.ToInt64(Pragma(context, "foreign_keys")) == 1, "foreign_keys must be on"));

    /// <summary>
    /// EF 打开的连接每次都设完全同步（拦截器在打开后设）。SQLite 的默认值本来就是 FULL，只读 EF 连接分不出拦截器设没设，
    /// 所以再拿一条先降成 OFF 的连接过一遍打开后的核对（拦截器调的就是它），必须回到 FULL。
    /// </summary>
    public static Task SynchronousFull()
    {
        using var home = new TestHome(); var process = new TestProcess(home.Path); var database = process.Database;
        using (var scope = process.Get<IServiceScopeFactory>().CreateScope())
            Check(Convert.ToInt64(Pragma(scope.ServiceProvider.GetRequiredService<RelayDbContext>(), "synchronous")) == 2, "synchronous must be FULL");
        using var raw = new SqliteConnection(database.ConnectionString);
        raw.Open(); RelayDatabase.Execute(raw, null, "PRAGMA synchronous=OFF;");
        database.Verify(raw);
        Check(Convert.ToInt64(RelayDatabase.Scalar(raw, null, "PRAGMA synchronous")) == 2, "The check after opening must set synchronous back to FULL");
        return Task.CompletedTask;
    }

    /// <summary>库文件是 WAL 日志模式（建库时设一次即持久）。</summary>
    public static Task WriteAheadLog() => WithContext((context, _) => Check((string)Pragma(context, "journal_mode") == "wal", "journal_mode must be WAL"));

    /// <summary>EF 用的连接串不用连接池（每次打开都重新核对实例）。</summary>
    public static Task PoolingOff() => WithContext((context, _) => Check(!new SqliteConnectionStringBuilder(context.Database.GetConnectionString()).Pooling, "Pooling must be off"));

    /// <summary>EF 用的连接串遇锁最多等 10 秒，且只读写已有文件（文件不在绝不悄悄建空库）。</summary>
    public static Task DefaultTimeout() => WithContext((context, _) =>
    {
        var builder = new SqliteConnectionStringBuilder(context.Database.GetConnectionString());
        Check(builder.DefaultTimeout == 10 && builder.Mode == SqliteOpenMode.ReadWrite, $"DefaultTimeout={builder.DefaultTimeout}, Mode={builder.Mode}");
    });

    /// <summary>实例标记被换成别的编号时，EF 经仓储读库被拒（打开前核对），读出恢复备份的提示。</summary>
    public static Task MarkerChecked() => WithContext((context, home) =>
    {
        File.WriteAllText(Path.Combine(home, ".relay-instance"), Guid.NewGuid().ToString("N"));
        Check(Refuses(() => context.Set<Core.Identity.User>().CountAsync()).Contains("实例数据不可用"), "A foreign instance marker must be refused");
    });

    /// <summary>库里的 instance_id 被换掉时，EF 打开连接后核对不过被拒，读出恢复配套备份的提示。</summary>
    public static Task InstanceIdChecked() => WithContext((context, home) =>
    {
        using (var raw = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabaseUpgradeTests.Database(home), Pooling = false }.ToString()))
        { raw.Open(); RelayDatabase.Execute(raw, null, "UPDATE metadata SET value='0123456789abcdef0123456789abcdef' WHERE key='instance_id'"); }
        Check(Refuses(() => context.Set<Core.Identity.User>().CountAsync()).Contains("数据库不属于当前实例"), "A database of another instance must be refused");
    });

    /// <summary>
    /// 两个作用域同时改同一家上游：都先读出，一个先提交，后提交的那个按读出时的行版本核对不过，收到 <see cref="DbUpdateConcurrencyException"/>，库里留的是先提交的那一份。
    /// 应用层把同类冲突读成中文：库里给这家上游的改写挂一个「什么都不改」的触发器（影响行数为 0，与被别人改过同一种信号），改名时读出「刚被另一页面或命令改过」。
    /// </summary>
    public static async Task ConcurrentUpstreamEdits()
    {
        using var home = new TestHome(); var process = new TestProcess(home.Path);
        var version = (await process.Upstreams.ReadOverview()).Version;
        var id = await process.Upstreams.SaveCandidate(version, new UpstreamOptions { BaseUrl = "https://race.example.com" }, "sk-race-0001", "甲家");
        var scopes = process.Get<IServiceScopeFactory>();
        await using (var first = scopes.CreateAsyncScope())
        await using (var second = scopes.CreateAsyncScope())
        {
            var mine = first.ServiceProvider.GetRequiredService<IRepository<UpstreamCandidate>>(); var theirs = second.ServiceProvider.GetRequiredService<IRepository<UpstreamCandidate>>();
            var a = (await mine.GetByIdAsync(id))!; var b = (await theirs.GetByIdAsync(id))!;
            a.Name = "先提交"; await mine.SaveChangesAsync();
            b.Name = "后提交"; var conflicted = false;
            try { await theirs.SaveChangesAsync(); }
            // 后提交的一方按读出时的行版本核对，对不上就是并发冲突；接住它就是要验证的结果。
            catch (DbUpdateConcurrencyException) { conflicted = true; }
            Check(conflicted, "The later commit must get DbUpdateConcurrencyException");
        }
        Check((await process.Upstreams.ReadCandidate(id)).Name == "先提交", "The first commit must survive");
        using (var connection = process.Database.Open())
            RelayDatabase.Execute(connection, null, "CREATE TRIGGER ignore_candidate_update BEFORE UPDATE ON upstream_candidates BEGIN SELECT RAISE(IGNORE); END");
        string? message = null;
        try { await process.Upstreams.Rename(id, "改不成"); }
        // 影响行数为 0 时 EF 抛并发冲突，应用层要把它读成中文下一步；接住的读句就是要验证的结果。
        catch (InvalidOperationException error) { message = error.Message; }
        Check(message == "这个上游刚被另一页面或命令改过，请重新加载页面再试。", message ?? "Rename must be refused");
    }

    /// <summary>用量 40 天清理是物理删除：过期用量连同它的上游尝试真从库里消失（不留软删除行），窗口内的照常保留。</summary>
    public static async Task CleanupDeletesPhysically()
    {
        using var home = new TestHome(); var process = new TestProcess(home.Path);
        var token = await process.Identities.Create("清理"); var user = (await process.Identities.Clients.Authenticate(token.Token))!;
        var version = (await process.Upstreams.ReadOverview()).Version;
        foreach (var id in new[] { "old-1", "old-2", "fresh" })
        {
            await process.Usage.Start(id, user, UsageKind.Inference); await process.Usage.Dispatch(id, version, null, "m");
            Check(await process.Usage.Finish(id, new UsageEnd(UsageOutcome.Succeeded, true, 200, 200, 1, UsageCompleteness.Complete, new(1, 2)) { Attempts = [new UpstreamAttempt(1, null, version, DateTimeOffset.UtcNow)] }) == Core.Usage.UsageFinish.Saved);
        }
        using (var connection = process.Database.Open())
            RelayDatabase.Execute(connection, null, "UPDATE usage_requests SET request_started_at=$old WHERE request_id LIKE 'old-%'", ("$old", DateTimeOffset.UtcNow.AddDays(-41).UtcTicks));
        Check(await process.Usage.Cleanup() == 2, "Two expired rows are cleaned");
        using (var connection = process.Database.Open())
        {
            Check(Convert.ToInt64(RelayDatabase.Scalar(connection, null, "SELECT COUNT(*) FROM usage_requests")) == 1
                && (string)RelayDatabase.Scalar(connection, null, "SELECT request_id FROM usage_requests")! == "fresh", "Expired usage must be gone from the table, not soft-deleted");
            Check(Convert.ToInt64(RelayDatabase.Scalar(connection, null, "SELECT COUNT(*) FROM usage_attempts")) == 1, "Their attempts go with them");
            Check(Convert.ToInt64(RelayDatabase.Scalar(connection, null, "SELECT COUNT(*) FROM usage_requests WHERE is_deleted=1")) == 0);
        }
    }

    /// <summary>停用（删除）的上游是软删除：普通查询读不到，<c>includeDeleted</c> 读得到，库里那一行 is_deleted=1、deleted_at 有值、密钥清成零长度。</summary>
    public static async Task DeletedUpstreamIsSoft()
    {
        using var home = new TestHome(); var process = new TestProcess(home.Path);
        var version = (await process.Upstreams.ReadOverview()).Version;
        var id = await process.Upstreams.SaveCandidate(version, new UpstreamOptions { BaseUrl = "https://soft.example.com" }, "sk-soft-0001", "软删");
        await process.Upstreams.Delete(id);
        await using (var scope = process.Get<IServiceScopeFactory>().CreateAsyncScope())
        {
            var candidates = scope.ServiceProvider.GetRequiredService<IRepository<UpstreamCandidate>>();
            Check(await candidates.GetByIdAsync(id) is null, "A deleted upstream is invisible to ordinary queries");
            Check(await candidates.GetByIdAsync(id, includeDeleted: true) is { IsDeleted: true, DeletedAt: not null, Credential.Length: 0 }, "includeDeleted reads it back");
        }
        using var connection = process.Database.Open();
        Check(Convert.ToInt64(RelayDatabase.Scalar(connection, null, "SELECT is_deleted FROM upstream_candidates WHERE id=$id", ("$id", id))) == 1
            && RelayDatabase.Scalar(connection, null, "SELECT deleted_at FROM upstream_candidates WHERE id=$id", ("$id", id)) is long, "The row stays with is_deleted and deleted_at");
    }

    /// <summary>
    /// 真实 v9 旧库夹具升到 11：已保存上游的 created_at、deleted_at 原值不变，is_deleted 与 deleted_at 一致；用量与尝试的业务时刻不变，创建时刻按它们换算；
    /// 按名字存的三张表名字与值逐条不变，并得到整数主键；尝试表改整数主键后同一用量内次序仍唯一；每行都有行版本；整库外键检查 0 条。
    /// </summary>
    public static Task Version11KeepsValues()
    {
        using var home = DatabaseUpgradeTests.Legacy("v9"); var path = DatabaseUpgradeTests.Database(home.Path);
        // 版本 9 的已保存上游主键就是对外编号（文本 id），版本 10 起它留成 uid 列。
        var candidates = Rows(path, "SELECT id||'|'||created_at||'|'||coalesce(deleted_at,'') FROM upstream_candidates");
        var usage = Rows(path, "SELECT request_id||'|'||request_started_at||'|'||coalesce(ended_at,'') FROM usage_requests");
        var named = Rows(path, "SELECT 'metadata|'||key||'|'||value FROM metadata WHERE key<>'last_upgrade' UNION ALL SELECT 'public|'||name||'|'||value FROM public_assets UNION ALL SELECT 'secret|'||name||'|'||hex(protected) FROM secrets");
        _ = new TestProcess(home.Path).Database;
        Check(Rows(path, "SELECT uid||'|'||created_at||'|'||coalesce(deleted_at,'') FROM upstream_candidates").SetEquals(candidates), "Upstream created_at/deleted_at must be unchanged");
        Check(Rows(path, "SELECT request_id||'|'||request_started_at||'|'||coalesce(ended_at,'') FROM usage_requests").SetEquals(usage), "Usage times must be unchanged");
        Check(Rows(path, "SELECT 'metadata|'||key||'|'||value FROM metadata WHERE key<>'last_upgrade' UNION ALL SELECT 'public|'||name||'|'||value FROM public_assets UNION ALL SELECT 'secret|'||name||'|'||hex(protected) FROM secrets").SetEquals(named),
            "Named rows must keep their names and values");
        using var connection = DatabaseUpgradeTests.Connect(path);
        long Count(string sql) => Convert.ToInt64(RelayDatabase.Scalar(connection, null, sql));
        Check(Count("PRAGMA user_version") == 11, "Version must be 11");
        Check(Count("SELECT COUNT(*) FROM upstream_candidates WHERE is_deleted <> (deleted_at IS NOT NULL)") == 0 && Count("SELECT COUNT(*) FROM upstream_candidates WHERE is_deleted=1") > 0,
            "is_deleted follows deleted_at, and the fixture has a deleted upstream");
        Check(Count("SELECT COUNT(*) FROM usage_requests WHERE created_at <> (request_started_at-621355968000000000)/10000") == 0, "Usage created_at is converted from its start time");
        foreach (var table in new[] { "metadata", "secrets", "public_assets", "usage_attempts", "site_identity_operations" })
            Check((string)RelayDatabase.Scalar(connection, null, $"SELECT type FROM pragma_table_info('{table}') WHERE pk=1")! == "INTEGER", table + " must have an integer key");
        Check(Count("SELECT COUNT(*) FROM (SELECT usage_request_id,attempt FROM usage_attempts GROUP BY 1,2 HAVING COUNT(*)>1)") == 0);
        foreach (var table in new[] { "metadata", "settings", "administrators", "bootstrap", "users", "tokens", "upstream", "secrets", "public_assets", "admin_sessions",
            "upstream_candidates", "usage_prices", "usage_requests", "usage_attempts", "site_identity_operations", "site_identity" })
            Check(Count($"SELECT COUNT(*) FROM {table} WHERE row_version IS NULL") == 0, table + " rows need a row version");
        Check(Count("SELECT COUNT(*) FROM pragma_foreign_key_check") == 0, "Foreign keys must all hold");
        return Task.CompletedTask;
    }

    /// <summary>v10→v11 半途失败整体回滚：v9 夹具第一次升级在版本 10 提交，第二次的注入点抛出，库停在 10，没有任何 _v11 残表，表结构仍是版本 10 的。</summary>
    public static Task Version11Rollback()
    {
        using var home = DatabaseUpgradeTests.Legacy("v9"); var path = DatabaseUpgradeTests.Database(home.Path);
        var calls = 0; var rejected = false;
        try { _ = new TestProcess(home.Path, migrationCheckpoint: () => { if (++calls == 2) throw new IOException("fixture-v11-checkpoint"); }).Database; }
        // 注入点抛出的就是要验证的失败；接住后检查库停在哪一版。
        catch (IOException error) when (error.Message == "fixture-v11-checkpoint") { rejected = true; }
        Check(rejected && calls == 2, "The second checkpoint (version 11) must abort the upgrade");
        using var connection = DatabaseUpgradeTests.Connect(path);
        Check(Convert.ToInt32(RelayDatabase.Scalar(connection, null, "PRAGMA user_version")) == 10, "Version 10 must remain");
        Check(Convert.ToInt32(RelayDatabase.Scalar(connection, null, "SELECT COUNT(*) FROM sqlite_master WHERE name LIKE '%_v11'")) == 0
            && Convert.ToInt32(RelayDatabase.Scalar(connection, null, "SELECT COUNT(*) FROM pragma_table_info('users') WHERE name='row_version'")) == 0, "Nothing of version 11 may remain");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 命令行 <c>--upgrade-relations 数据目录副本</c>：先量副本里的六类关系与九张表行数，再按正式组合根打开（升到当前版本），升完再量，逐类报条数与对称差、外键检查条数。
    /// 只用于复制出来的副本（正式库按文件复制到临时目录再跑，原件不动）。对称差全为 0、外键检查 0 条时退出码 0。
    /// </summary>
    public static Task<int> UpgradeRelations(string[] args)
    {
        var home = args.ElementAtOrDefault(1) ?? throw new InvalidOperationException("用法：--upgrade-relations <数据目录副本>");
        var path = DatabaseUpgradeTests.Database(home);
        var before = IntegerKeyUpgradeTests.Relations(path); var rows = IntegerKeyUpgradeTests.Rows(path);
        int version; using (var connection = DatabaseUpgradeTests.Connect(path)) version = Convert.ToInt32(RelayDatabase.Scalar(connection, null, "PRAGMA user_version"));
        _ = new TestProcess(home).Database;
        var after = IntegerKeyUpgradeTests.Relations(path); var afterRows = IntegerKeyUpgradeTests.Rows(path);
        var clean = true;
        Console.WriteLine($"版本 {version} → {RelayDatabase.CurrentVersion}");
        foreach (var (kind, set) in before)
        {
            var differ = set.Except(after[kind]).Count() + after[kind].Except(set).Count(); clean &= differ == 0;
            Console.WriteLine($"关系 {kind}：升级前 {set.Count} 条，升级后 {after[kind].Count} 条，对称差 {differ}");
        }
        foreach (var (table, count) in rows) { clean &= afterRows[table] == count; Console.WriteLine($"行数 {table}：{count} → {afterRows[table]}"); }
        using (var connection = DatabaseUpgradeTests.Connect(path))
        {
            var broken = Convert.ToInt64(RelayDatabase.Scalar(connection, null, "SELECT COUNT(*) FROM pragma_foreign_key_check")); clean &= broken == 0;
            Console.WriteLine($"外键检查：{broken} 条对不上");
        }
        return Task.FromResult(clean ? 0 : 1);
    }

    /// <summary>
    /// 比对 EF 模型与一个库的实际表结构，返回每一处不一致的说明（空列表表示一致）。migration_archive 只有迁移链读写、不是运行时数据，不在模型里；
    /// one_administrator 是表达式唯一索引（最多一名管理员），EF 模型表达不了，单列在白名单里。
    /// </summary>
    internal static List<string> SchemaDifferences(IModel model, SqliteConnection connection)
    {
        var differences = new List<string>();
        var entities = model.GetEntityTypes().ToDictionary(entity => entity.GetTableName()!, StringComparer.Ordinal);
        var tables = Strings(connection, "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name<>'migration_archive'");
        foreach (var missing in tables.Except(entities.Keys)) differences.Add($"表 {missing} 在库里有、模型里没有");
        foreach (var missing in entities.Keys.Except(tables)) differences.Add($"表 {missing} 在模型里有、库里没有");
        foreach (var (table, entity) in entities.Where(item => tables.Contains(item.Key)))
        {
            var store = StoreObjectIdentifier.Table(table);
            var modelColumns = entity.GetProperties().ToDictionary(property => property.GetColumnName(store)!, property => (Nullable: property.IsColumnNullable(store), Key: property.IsPrimaryKey()));
            var schemaColumns = new Dictionary<string, (bool Nullable, bool Key)>();
            using (var command = RelayDatabase.Command(connection, null, "SELECT name,\"notnull\",pk FROM pragma_table_info($table)", ("$table", table)))
            using (var reader = command.ExecuteReader())
                while (reader.Read()) schemaColumns[reader.GetString(0)] = (reader.GetInt64(1) == 0, reader.GetInt64(2) > 0);
            foreach (var column in schemaColumns.Keys.Except(modelColumns.Keys)) differences.Add($"{table}.{column} 在库里有、模型里没有");
            foreach (var column in modelColumns.Keys.Except(schemaColumns.Keys)) differences.Add($"{table}.{column} 在模型里有、库里没有");
            foreach (var (column, shape) in modelColumns.Where(item => schemaColumns.ContainsKey(item.Key)))
            {
                var actual = schemaColumns[column];
                if (shape.Key != actual.Key) differences.Add($"{table}.{column} 主键不一致");
                // INTEGER PRIMARY KEY 在 table_info 里不标非空，主键列只比是不是主键。
                else if (!shape.Key && shape.Nullable != actual.Nullable) differences.Add($"{table}.{column} 可空不一致：模型 {shape.Nullable}，库 {actual.Nullable}");
            }
            var modelKeys = entity.GetForeignKeys().Select(key => string.Join(",", key.Properties.Select(property => property.GetColumnName(store))) + "->"
                + key.PrincipalEntityType.GetTableName() + "(" + string.Join(",", key.PrincipalKey.Properties.Select(property => property.GetColumnName(StoreObjectIdentifier.Table(key.PrincipalEntityType.GetTableName()!)))) + ")"
                + (key.DeleteBehavior == DeleteBehavior.Cascade ? " cascade" : "")).ToHashSet(StringComparer.Ordinal);
            var schemaKeys = new Dictionary<long, (List<string> From, string Table, List<string> To, string OnDelete)>();
            using (var command = RelayDatabase.Command(connection, null, "SELECT id,\"table\",\"from\",\"to\",on_delete FROM pragma_foreign_key_list($table) ORDER BY id,seq", ("$table", table)))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                {
                    if (!schemaKeys.TryGetValue(reader.GetInt64(0), out var key)) schemaKeys[reader.GetInt64(0)] = key = ([], reader.GetString(1), [], reader.GetString(4));
                    key.From.Add(reader.GetString(2)); key.To.Add(reader.GetString(3));
                }
            var actualKeys = schemaKeys.Values.Select(key => string.Join(",", key.From) + "->" + key.Table + "(" + string.Join(",", key.To) + ")" + (key.OnDelete == "CASCADE" ? " cascade" : "")).ToHashSet(StringComparer.Ordinal);
            foreach (var key in actualKeys.Except(modelKeys)) differences.Add($"{table} 外键 {key} 在库里有、模型里没有");
            foreach (var key in modelKeys.Except(actualKeys)) differences.Add($"{table} 外键 {key} 在模型里有、库里没有");
            var modelUnique = entity.GetIndexes().Where(index => index.IsUnique).Select(index => string.Join(",", index.Properties.Select(property => property.GetColumnName(store))) + (index.GetFilter() is null ? "" : " partial")).ToHashSet(StringComparer.Ordinal);
            var modelPlain = entity.GetIndexes().Where(index => !index.IsUnique).Select(index => index.GetDatabaseName()!).ToHashSet(StringComparer.Ordinal);
            var actualUnique = new HashSet<string>(StringComparer.Ordinal); var actualPlain = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (name, unique, origin, partial) in Indexes(connection, table))
            {
                if (origin == "pk") continue;
                var columns = Strings(connection, $"SELECT coalesce(name,'<expression>') FROM pragma_index_info('{name}') ORDER BY seqno").ToList();
                if (columns.Contains("<expression>")) { if (name != "one_administrator") differences.Add($"{table} 有模型表达不了的表达式索引 {name}"); continue; }
                if (unique) actualUnique.Add(string.Join(",", columns) + (partial ? " partial" : "")); else actualPlain.Add(name);
            }
            foreach (var index in actualUnique.Except(modelUnique)) differences.Add($"{table} 唯一约束 ({index}) 在库里有、模型里没有");
            foreach (var index in modelUnique.Except(actualUnique)) differences.Add($"{table} 唯一约束 ({index}) 在模型里有、库里没有");
            foreach (var index in actualPlain.Except(modelPlain)) differences.Add($"{table} 索引 {index} 在库里有、模型里没有");
            foreach (var index in modelPlain.Except(actualPlain)) differences.Add($"{table} 索引 {index} 在模型里有、库里没有");
        }
        return differences;
    }

    /// <summary>一张表的索引：名字、是否唯一、来源（c 建索引语句、u 唯一约束、pk 主键）、是否带条件。</summary>
    private static List<(string Name, bool Unique, string Origin, bool Partial)> Indexes(SqliteConnection connection, string table)
    {
        var result = new List<(string, bool, string, bool)>();
        using var command = RelayDatabase.Command(connection, null, "SELECT name,\"unique\",origin,partial FROM pragma_index_list($table)", ("$table", table));
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add((reader.GetString(0), reader.GetInt64(1) == 1, reader.GetString(2), reader.GetInt64(3) == 1));
        return result;
    }

    /// <summary>查出一列文本。</summary>
    private static HashSet<string> Strings(SqliteConnection connection, string sql)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        using var command = RelayDatabase.Command(connection, null, sql);
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    /// <summary>在一份库文件上查出一列文本（NULL 记成固定标记）。</summary>
    private static HashSet<string> Rows(string path, string sql)
    {
        using var connection = DatabaseUpgradeTests.Connect(path);
        var result = new HashSet<string>(StringComparer.Ordinal);
        using var command = RelayDatabase.Command(connection, null, sql);
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(reader.IsDBNull(0) ? "<null>" : Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!);
        return result;
    }

    /// <summary>新建一个临时实例，取它的 EF 上下文交给 <paramref name="check"/>（第二个参数是数据目录）。</summary>
    private static Task WithContext(Action<RelayDbContext, string> check)
    {
        using var home = new TestHome(); var process = new TestProcess(home.Path); _ = process.Database;
        using var scope = process.Get<IServiceScopeFactory>().CreateScope();
        check(scope.ServiceProvider.GetRequiredService<RelayDbContext>(), home.Path);
        return Task.CompletedTask;
    }

    /// <summary>经 EF 打开的连接读一个 PRAGMA（拦截器在打开时生效）。</summary>
    private static object Pragma(RelayDbContext context, string name)
    {
        context.Database.OpenConnection();
        try
        {
            using var command = context.Database.GetDbConnection().CreateCommand(); command.CommandText = "PRAGMA " + name;
            return command.ExecuteScalar()!;
        }
        // 只读一个值，读完归还连接，不留给后面的检查。
        finally { context.Database.CloseConnection(); }
    }

    /// <summary>执行一次读库，期望被拒并返回拒绝读句；没被拒时返回空串，让断言失败。</summary>
    private static string Refuses(Func<Task<int>> read)
    {
        try { read().GetAwaiter().GetResult(); return ""; }
        // 被拒就是要验证的结果：只接住实例核对抛出的那一类，读句交给断言比对。
        catch (InvalidOperationException error) { return error.Message; }
    }
}
