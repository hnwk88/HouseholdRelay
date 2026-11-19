using Microsoft.Data.Sqlite;
using static Tests.TestSuite;

namespace Tests;

/// <summary>
/// 库版本 9：真实旧库升级逐项对数、升级前备份、迁移另存、上游停用（软删除）与外键。旧库夹具由旧版正式程序生成（tools/legacy-fixtures，来源提交见各目录 SOURCE.txt），
/// 不删表删列、不改版本号伪造；用例把它复制进自己的临时数据目录再升级，原件不动。
/// </summary>
static class DatabaseUpgradeTests
{
    /// <summary>
    /// 版本 8 真实旧库（提交 2dd823c 生成：三家上游、一家改过名、一家被物理删除留下孤儿）升到 9：各表行数相等；单价少掉的正好是另存的孤儿单价；
    /// 用量和尝试按上游归属的条数，只有孤儿那一组挪到「无上游」，差额等于另存的悬空引用；升级前备份是完整的版本 8 库；三张表对已保存上游有外键；读句不含本机用户名。
    /// </summary>
    public static Task RealVersion8()
    {
        using var home = Legacy("v8");
        var before = Snapshot(Database(home.Path));
        var process = new TestProcess(home.Path); var database = process.Database;
        var after = Snapshot(database.Path);
        Check(Version(database.Path) == RelayDatabase.CurrentVersion && before.Version == 8);
        var archived = Archived(database.Path);
        Check(archived.GetValueOrDefault("usage_prices/orphan-price") == 2, "The deleted upstream left two prices");
        Check(archived.GetValueOrDefault("usage_requests/orphan-reference") == 1 && archived.GetValueOrDefault("usage_attempts/orphan-reference") == 2);
        foreach (var table in new[] { "users", "tokens", "upstream_candidates", "usage_requests", "usage_attempts", "settings", "site_identity" })
            Check(after.Rows[table] == before.Rows[table], table + " row count changed");
        Check(after.Rows["usage_prices"] == before.Rows["usage_prices"] - archived["usage_prices/orphan-price"]);
        foreach (var table in new[] { "usage_requests", "usage_attempts" })
        {
            var orphan = archived[table + "/orphan-reference"];
            var moved = before.ByUpstream[table].Where(item => !after.ByUpstream[table].ContainsKey(item.Key)).Sum(item => item.Value);
            Check(moved == orphan && after.ByUpstream[table].GetValueOrDefault("") == before.ByUpstream[table].GetValueOrDefault("") + orphan, table + " ownership moved more than archived");
            foreach (var (candidate, count) in after.ByUpstream[table].Where(item => item.Key.Length != 0)) Check(before.ByUpstream[table][candidate] == count);
        }
        using (var connection = Connect(database.Path))
            foreach (var table in new[] { "usage_prices", "usage_requests", "usage_attempts" })
                Check(Convert.ToInt32(RelayDatabase.Scalar(connection, null, $"SELECT COUNT(*) FROM pragma_foreign_key_list('{table}') WHERE \"table\"='upstream_candidates'")) == 1, table + " lacks the upstream foreign key");
        var upgrade = process.Get<IRelayInstance>().LastUpgrade!;
        Check(upgrade.FromVersion == 8 && upgrade.ToVersion == RelayDatabase.CurrentVersion && process.Get<IRelayInstance>().UpgradedOnOpen);
        var backup = Path.Combine(home.Path, upgrade.Backup);
        Check(upgrade.Backup.StartsWith(".state", StringComparison.Ordinal) && File.Exists(backup) && Version(backup) == 8 && Snapshot(backup).Rows.SequenceEqual(before.Rows), "Backup must be the complete version 8 database");
        Check(!upgrade.Sentence().Contains(Environment.UserName, StringComparison.OrdinalIgnoreCase) && upgrade.Sentence().Contains($"从版本 8 升到 {RelayDatabase.CurrentVersion}") && upgrade.Sentence().Contains("指向已删除上游的单价 2 条"), upgrade.Sentence());
        // 再开一次不再升级、不再备份。
        var again = new TestProcess(home.Path);
        Check(!again.Get<IRelayInstance>().UpgradedOnOpen && Directory.GetDirectories(Path.Combine(home.Path, ".state", "upgrade-backups")).Length == 1);
        // 旧记录没有名称快照，读当前名字（含改过的名字）。
        var rows = again.Usage.Query(new()).Attempts;
        Check(rows["fixture-v8-1"].Single().Name == "夹具乙供应商改名" && rows["fixture-v8-2"].Single().CandidateId is null);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 版本 3 真实旧库（提交 39c6785 生成：三家上游两家带不同代理、两条按活动版本计的单价、六条用量）一路升到 9：
    /// 使用者、令牌、上游、用量条数不变；活动上游没有来源，版本 6 搬不过去的两条单价整行另存；没被选作全局代理的那个旧代理地址原值另存。
    /// </summary>
    public static Task RealVersion3()
    {
        using var home = Legacy("v3");
        var before = Snapshot(Database(home.Path));
        var process = new TestProcess(home.Path); var database = process.Database;
        var after = Snapshot(database.Path);
        Check(before.Version == 3 && Version(database.Path) == RelayDatabase.CurrentVersion);
        foreach (var table in new[] { "users", "tokens", "upstream_candidates", "usage_requests", "settings" })
            Check(after.Rows[table] == before.Rows[table], table + " row count changed");
        var archived = Archived(database.Path);
        Check(before.Rows["usage_prices"] == 2 && after.Rows["usage_prices"] == 0 && archived.GetValueOrDefault("usage_prices/price-not-migrated") == 2, "Prices that cannot be rekeyed are archived, not dropped");
        Check(archived.GetValueOrDefault("upstream_candidates/dropped-proxy") == 1);
        Check(process.Settings.Read().Value.ProxyUrl == "http://127.0.0.1:7891");
        using (var connection = Connect(database.Path))
            Check(((string)RelayDatabase.Scalar(connection, null, "SELECT row_json FROM migration_archive WHERE reason='dropped-proxy'")!).Contains("http://127.0.0.1:7890"));
        Check(process.Upstreams.ListCandidates().Select(item => item.Name).SequenceEqual(["relay-a.example.com", "relay-b.example.com", "relay-c.example.com"]));
        Check(process.Get<IRelayInstance>().LastUpgrade!.FromVersion == 3);
        return Task.CompletedTask;
    }

    /// <summary>真实版本 3 旧库升级中途失败：整体回滚，库仍是版本 3、各表条数不变，备份已经做好；去掉故障后照常升到 9。</summary>
    public static Task RealVersion3Rollback()
    {
        using var home = Legacy("v3");
        var before = Snapshot(Database(home.Path));
        var rejected = false;
        try { _ = new TestProcess(home.Path, migrationCheckpoint: () => throw new IOException("fixture-checkpoint")).Database; }
        // 第一个迁移就在提交前失败，整个升级应回滚；随后核对版本号与条数。
        catch (IOException error) when (error.Message == "fixture-checkpoint") { rejected = true; }
        Check(rejected && Version(Database(home.Path)) == 3 && Snapshot(Database(home.Path)).Rows.SequenceEqual(before.Rows));
        Check(Directory.GetFiles(Path.Combine(home.Path, ".state", "upgrade-backups"), "relay.db", SearchOption.AllDirectories).Length == 1);
        var upgraded = new TestProcess(home.Path);
        Check(Version(upgraded.Database.Path) == RelayDatabase.CurrentVersion && upgraded.Usage.Query(new() { From = DateTimeOffset.UtcNow.AddDays(-1) }).Total == 6);
        return Task.CompletedTask;
    }

    /// <summary>备份做不成就不升级：备份目录的位置被一个同名文件占住时启动报错、读出「没有升级」，库仍是版本 8。</summary>
    public static Task BackupFailureBlocksUpgrade()
    {
        using var home = Legacy("v8");
        File.WriteAllText(Path.Combine(home.Path, ".state", "upgrade-backups"), "occupied");
        var message = "";
        try { _ = new TestProcess(home.Path).Database; }
        // 备份失败按设计抛出读给管理员的固定说明。
        catch (InvalidOperationException error) { message = error.Message; }
        Check(message.Contains("没有升级", StringComparison.Ordinal) && Version(Database(home.Path)) == 8, message);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 版本 4 迁移找活动来源：真实版本 3 旧库里两家已激活上游与活动行原文一致时不猜，来源留空、不补建，两个主键另存；
    /// 地址解析不出主机名的那家名称留空，页面读「未命名上游」。旧库里这两种状态要靠测试通过才激活，这里在真实旧表上直接写数据行，不改结构。
    /// </summary>
    public static Task AmbiguousSourceAndUnnamed()
    {
        using var home = Legacy("v3");
        string first, second;
        using (var connection = Connect(Database(home.Path)))
        {
            var ids = new List<string>();
            using (var command = RelayDatabase.Command(connection, null, "SELECT id FROM upstream_candidates ORDER BY created_at,rowid"))
            using (var reader = command.ExecuteReader()) while (reader.Read()) ids.Add(reader.GetString(0));
            (first, second) = (ids[0], ids[1]);
            const string report = """{"checkedAt":"2026-10-28T14:03:14Z","connection":"passed","authentication":"passed","modelDiscovery":"passed","call":"passed","models":[],"verifiedModel":"claude-fixture","message":"ok"}""";
            RelayDatabase.Execute(connection, null, """
                UPDATE upstream_candidates SET state='activated',report_json=$report,options_json=(SELECT options_json FROM upstream_candidates WHERE id=$first) WHERE id IN ($first,$second);
                UPDATE upstream SET options_json=(SELECT options_json FROM upstream_candidates WHERE id=$first),report_json=$report,credential=(SELECT credential FROM upstream_candidates WHERE id=$first) WHERE id=1;
                UPDATE upstream_candidates SET options_json=json_set(options_json,'$.baseUrl','not an address') WHERE id=$third;
                """, ("$report", report), ("$first", first), ("$second", second), ("$third", ids[2]));
        }
        var process = new TestProcess(home.Path);
        Check(process.Upstreams.ReadOverview().CandidateId is null && process.Upstreams.ListCandidates().Count == 3, "Ambiguous source must not be guessed or duplicated");
        using (var connection = Connect(process.Database.Path))
        {
            var kept = new List<string>();
            using (var command = RelayDatabase.Command(connection, null, "SELECT json_extract(row_json,'$.candidate_id') FROM migration_archive WHERE reason='ambiguous-active-source' ORDER BY 1"))
            using (var reader = command.ExecuteReader()) while (reader.Read()) kept.Add(reader.GetString(0));
            Check(kept.SequenceEqual(new[] { first, second }.Order(StringComparer.Ordinal)));
            Check((string?)RelayDatabase.Scalar(connection, null, "SELECT name FROM upstream_candidates ORDER BY created_at DESC,rowid DESC LIMIT 1") == "");
        }
        Check(process.Upstreams.ListCandidates()[2].Name == "未命名上游");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 删除已保存上游是停用：返回引用它的用量、尝试、单价条数；它从列表消失、凭据清空，单价与用量照常引用它；
    /// 用量页读出记账那一刻的名字加「（已删除）」（删除前改过名也读旧名）；外键拦住指向不存在上游的单价；停用的上游不能再存单价。
    /// </summary>
    public static async Task SoftDeleteKeepsHistory()
    {
        using var home = new TestHome(); var process = new TestProcess(home.Path); var token = process.Identities.Create("停用");
        var user = process.Identities.Clients.Authenticate(token.Token)!; var version = process.Upstreams.ReadOverview().Version;
        var gone = process.Upstreams.SaveCandidate(version, new UpstreamOptions { BaseUrl = "https://gone.example.com" }, "sk-gone-0001", "顺丰中转");
        process.Pricing.Save(gone, "fixture-model", "USD", 1m, 2m, null, null);
        await process.Usage.Start("served", user, "inference"); await process.Usage.Dispatch("served", version, gone, "fixture-model");
        Check(await process.Usage.Finish("served", new UsageEnd("succeeded", true, 200, 200, 1, "complete", new(1, 2)) { Attempts = [new UpstreamAttempt(1, gone, version, DateTimeOffset.UtcNow)] }));
        process.Upstreams.Rename(gone, "顺丰中转改名");
        var references = process.Upstreams.Delete(gone);
        Check(references == new Application.Contracts.Upstream.UpstreamReferences(1, 1, 1), references.ToString());
        Check(process.Upstreams.ListCandidates().Count == 0);
        var missing = false;
        try { process.Upstreams.ReadCandidate(gone); }
        // 停用的上游按设计读作「不存在」，抛出提示重新加载的说明；接住它就是要验证的结果。
        catch (InvalidOperationException) { missing = true; }
        Check(missing, "A deleted upstream reads as gone everywhere else");
        using (var connection = process.Database.Open())
        {
            Check(Convert.ToInt32(RelayDatabase.Scalar(connection, null, "SELECT length(credential) FROM upstream_candidates WHERE id=$id", ("$id", gone))) == 0);
            Check(Convert.ToInt32(RelayDatabase.Scalar(connection, null, "SELECT COUNT(*) FROM usage_prices WHERE candidate_id=$id", ("$id", gone))) == 1);
            var blocked = false;
            try { RelayDatabase.Execute(connection, null, "INSERT INTO usage_prices(candidate_id,model,json) VALUES(999999,'m','{}')"); }
            // 外键约束失败是 SQLite 错误 19（约束）；用来证明指向不存在上游的单价写不进去。
            catch (SqliteException error) when (error.SqliteErrorCode == 19) { blocked = true; }
            Check(blocked, "Foreign key must reject a price for a missing upstream");
        }
        var attempt = process.Usage.Query(new()).Attempts["served"].Single();
        Check(attempt.Name == "顺丰中转" && UsageLabels.Attempts([attempt]).StartsWith("顺丰中转（已删除），", StringComparison.Ordinal), UsageLabels.Attempts([attempt]));
        // 停用的上游不能再存单价：保存在事务里核对那一家仍在用。
        var rejected = false;
        try { process.Pricing.Save(gone, "other-model", "USD", 1m, 2m, null, null); }
        // 按设计拒绝并读出原因；接住它就是要验证的结果。
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "A deleted upstream takes no new prices");
        Check(UsageLabels.Attempts([attempt with { Name = "", Deleted = false }]).StartsWith("未命名上游，", StringComparison.Ordinal));
    }

    /// <summary>
    /// 网页走一遍：给一家上游记一条用量和一条单价后，经上游页的删除表单删除它，页面读出「1 条用量记录、1 次上游尝试、1 条单价」；
    /// 再打开用量页，那次尝试读「顺丰中转（已删除），200，……」。
    /// </summary>
    public static async Task WebDeleteReadsReferences()
    {
        await using var web = await WebFixture.Create(); await web.Login();
        var token = web.Identities.Create("网页"); var user = web.Identities.Clients.Authenticate(token.Token)!;
        var version = web.Upstreams.ReadOverview().Version;
        var gone = web.Upstreams.SaveCandidate(version, new UpstreamOptions { BaseUrl = "https://gone.example.com" }, "sk-web-gone-0001", "顺丰中转");
        web.Pricing.Save(gone, "fixture-model", "USD", 1m, 2m, null, null);
        var usage = web.App.Services.GetRequiredService<IUsageService>();
        await usage.Start("web-served", user, "inference"); await usage.Dispatch("web-served", version, gone, "fixture-model");
        var attempt = new UpstreamAttempt(1, gone, version, DateTimeOffset.UtcNow); attempt.Headers(200, null, text => text);
        Check(await usage.Finish("web-served", new UsageEnd("succeeded", true, 200, 200, 1, "complete", new(1, 2)) { Attempts = [attempt] }));
        var form = new Dictionary<string, string> { ["candidateId"] = gone.ToString(CultureInfo.InvariantCulture), ["deleteConfirmed"] = "true", ["__RequestVerificationToken"] = await web.Csrf("/Upstream") };
        string notice;
        using (var response = await web.Post("/Upstream?handler=Delete", form)) notice = System.Net.WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Check(notice.Contains("已删除「顺丰中转」，本机已清除它的密钥。它有 1 条用量记录、1 次上游尝试、1 条单价，历史照常保留，用量页仍读出它的名字。"), "Delete notice must read the reference counts");
        Check(!notice.Contains("删除「顺丰中转」\""), "The deleted upstream leaves the list");
        var page = System.Net.WebUtility.HtmlDecode(await web.Http.GetStringAsync(web.Url + "/Usage"));
        Check(page.Contains("顺丰中转（已删除），200，"), "Usage page must read the deleted upstream's name");
    }

    /// <summary>升级前的库与升级后的库各量一遍的数：库版本、各表行数、用量与尝试按上游主键分组的条数（空串代表没有上游）。</summary>
    private sealed record Counts(int Version, Dictionary<string, long> Rows, Dictionary<string, Dictionary<string, long>> ByUpstream);

    /// <summary>把旧库夹具复制进一个新的临时数据目录：实例标记放数据目录，库放 .state；版本 9 夹具另带官方账户目录，一并放进 .state。</summary>
    internal static TestHome Legacy(string name)
    {
        var home = new TestHome(); CopyLegacy(name, home.Path);
        return home;
    }

    /// <summary>把旧库夹具 <paramref name="name"/> 放进已有的空数据目录 <paramref name="home"/>（网页夹具建站前也用它）：实例标记放数据目录，库与官方账户目录放 .state。</summary>
    internal static void CopyLegacy(string name, string home)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Fixtures", "LegacyDatabases", name);
        Directory.CreateDirectory(Path.Combine(home, ".state"));
        File.Copy(Path.Combine(source, ".relay-instance"), Path.Combine(home, ".relay-instance"));
        File.Copy(Path.Combine(source, "relay.db"), Database(home));
        var accounts = Path.Combine(source, "official-accounts");
        if (Directory.Exists(accounts))
            foreach (var file in Directory.GetFiles(accounts, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(home, ".state", "official-accounts", Path.GetRelativePath(accounts, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target);
            }
    }

    /// <summary>
    /// 复制旧库夹具后让程序自己把它升到正好 <paramref name="version"/>：从夹具版本往上，第 4 版起每版迁移在提交前各调一次检查点，
    /// 这里在第 <paramref name="version"/>+1 版的检查点抛错，那一版回滚、之前各版已各自提交。得到的是程序一路迁移出来的真实旧形状，
    /// 用例再在上面只写数据行、不改结构，用来代替「新库删列、改回版本号」的伪造旧库。
    /// </summary>
    internal static TestHome LegacyAt(string name, int version)
    {
        var home = Legacy(name); var from = Version(Database(home.Path));
        if (from < 3 || version < from || version >= RelayDatabase.CurrentVersion) throw new ArgumentOutOfRangeException(nameof(version));
        if (version > from)
        {
            var calls = 0; var stop = version - from + 1; var marker = "stop-at-v" + version;
            try { _ = new TestProcess(home.Path, migrationCheckpoint: () => { if (++calls == stop) throw new IOException(marker); }).Database; }
            // 按设计在下一版提交前停下；接住它，下面核对库版本正好是要的那一版。
            catch (IOException error) when (error.Message == marker) { }
        }
        Check(Version(Database(home.Path)) == version, $"Fixture {name} must stop at version {version}");
        return home;
    }

    /// <summary>数据目录下库文件的路径。</summary>
    internal static string Database(string home) => Path.Combine(home, ".state", "relay.db");

    /// <summary>不经程序、直接以读写方式打开库文件（不建池，用完即放）。</summary>
    internal static SqliteConnection Connect(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        connection.Open(); return connection;
    }

    /// <summary>库版本号。</summary>
    private static int Version(string path) { using var connection = Connect(path); return Convert.ToInt32(RelayDatabase.Scalar(connection, null, "PRAGMA user_version")); }

    /// <summary>量一份库：各业务表的行数，以及用量与尝试按上游主键分组的条数。</summary>
    private static Counts Snapshot(string path)
    {
        using var connection = Connect(path);
        var tables = new List<string>();
        using (var command = RelayDatabase.Command(connection, null, "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name<>'migration_archive' ORDER BY name"))
        using (var reader = command.ExecuteReader()) while (reader.Read()) tables.Add(reader.GetString(0));
        var rows = tables.ToDictionary(table => table, table => Convert.ToInt64(RelayDatabase.Scalar(connection, null, $"SELECT COUNT(*) FROM \"{table}\"")));
        var groups = new Dictionary<string, Dictionary<string, long>>();
        foreach (var table in new[] { "usage_requests", "usage_attempts" })
        {
            var group = groups[table] = [];
            if (!tables.Contains(table) || Convert.ToInt32(RelayDatabase.Scalar(connection, null, $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name='candidate_id'")) == 0) continue;
            // 版本 10 起引用是整数主键：经外键连回上游的对外编号（就是旧的文本主键）再分组，升级前后才比得上。
            var keyed = Convert.ToInt32(RelayDatabase.Scalar(connection, null, "PRAGMA user_version")) >= 10;
            using var command = RelayDatabase.Command(connection, null, keyed
                ? $"SELECT coalesce(c.uid,''),COUNT(*) FROM {table} t LEFT JOIN upstream_candidates c ON c.id=t.candidate_id GROUP BY 1"
                : $"SELECT coalesce(candidate_id,''),COUNT(*) FROM {table} GROUP BY 1");
            using var reader = command.ExecuteReader(); while (reader.Read()) group[reader.GetString(0)] = reader.GetInt64(1);
        }
        return new(Convert.ToInt32(RelayDatabase.Scalar(connection, null, "PRAGMA user_version")), rows, groups);
    }

    /// <summary>归档表按「原表/原因」汇总的条数；没有归档表时为空。</summary>
    private static Dictionary<string, long> Archived(string path)
    {
        using var connection = Connect(path); var result = new Dictionary<string, long>();
        using var command = RelayDatabase.Command(connection, null, "SELECT source_table||'/'||reason,COUNT(*) FROM migration_archive GROUP BY 1");
        using var reader = command.ExecuteReader(); while (reader.Read()) result[reader.GetString(0)] = reader.GetInt64(1);
        return result;
    }
}
