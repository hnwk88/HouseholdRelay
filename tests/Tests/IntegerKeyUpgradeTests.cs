using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using static Tests.TestSuite;

namespace Tests;

/// <summary>
/// 库版本 10：七张实体表主键改成库生成的整数、表间引用改成整数外键。起点是 bb20b32 程序生成的真实 v9 旧库夹具（tools/legacy-fixtures -V9），
/// 用例复制进自己的临时数据目录再升级，原件不动；夹具 plain.json 里的令牌、会话、导出密码都是假值。
/// </summary>
static class IntegerKeyUpgradeTests
{
    /// <summary>升级前后都要量的表：七张换主键的表加尝试记录与活动上游行。</summary>
    private static readonly string[] _tables = ["administrators", "users", "tokens", "upstream_candidates", "usage_prices", "admin_sessions", "usage_requests", "usage_attempts", "upstream"];

    /// <summary>
    /// 真实 v9 升到 10，逐项对数：九张表行数相等；关系逐条相等——用量（请求、使用者、令牌、上游）、尝试（请求、次序、上游）、单价（上游、模型）、令牌（令牌、使用者）、
    /// 会话（凭据哈希、管理员）、活动上游（来源），升级前直接读旧文本主键，升级后经整数外键连回对外编号或归档里的旧编号再读，两份集合完全相同；
    /// 整库外键检查 0 条；四张不留旧编号的表逐条另存且读句报数；升级前的个人令牌、管理员会话与密码照常可用，导入登录文件的官方账户仍读得出凭据，停用的上游仍是停用。
    /// </summary>
    public static async Task RealVersion9()
    {
        using var home = DatabaseUpgradeTests.Legacy("v9"); var path = DatabaseUpgradeTests.Database(home.Path); var plain = Plain();
        // 会话 8 小时到期：在旧表上只把到期时刻推后（数据行，不改结构），用来证明凭据哈希原样搬了过去。
        using (var connection = DatabaseUpgradeTests.Connect(path))
            RelayDatabase.Execute(connection, null, "UPDATE admin_sessions SET expires_at=$far", ("$far", DateTimeOffset.UtcNow.AddYears(1).ToUnixTimeSeconds()));
        var before = Relations(path); var rows = Rows(path);
        Check(Version(path) == 9 && rows["usage_requests"] == 7 && rows["usage_attempts"] == 7 && rows["tokens"] == 4 && rows["upstream_candidates"] == 4, "Fixture shape changed");
        var process = new TestProcess(home.Path); _ = process.Database;
        Check(Version(path) == RelayDatabase.CurrentVersion && RelayDatabase.CurrentVersion == 11);
        var after = Relations(path);
        foreach (var (kind, set) in before)
        {
            Check(set.Count > 0 && set.SetEquals(after[kind]), $"{kind}: {set.Count} before, {after[kind].Count} after, {set.Except(after[kind]).Count()} differ");
            Console.WriteLine($"KEY V10 RELATIONS {kind}: {set.Count} rows, {set.Except(after[kind]).Count() + after[kind].Except(set).Count()} differ");
        }
        foreach (var table in _tables) Check(Rows(path)[table] == rows[table], table + " row count changed");
        using (var connection = DatabaseUpgradeTests.Connect(path))
        {
            Check(Convert.ToInt32(RelayDatabase.Scalar(connection, null, "SELECT COUNT(*) FROM pragma_foreign_key_check")) == 0, "Foreign keys must all hold");
            foreach (var table in _tables.Where(table => table is not ("administrators" or "users" or "upstream_candidates")))
                Check(Convert.ToInt32(RelayDatabase.Scalar(connection, null, $"SELECT COUNT(*) FROM pragma_foreign_key_list('{table}')")) > 0, table + " lacks its foreign keys");
            Check((string)RelayDatabase.Scalar(connection, null, "SELECT type FROM pragma_table_info('usage_requests') WHERE pk=1")! == "INTEGER");
        }
        var archived = Archived(path);
        Check(archived.GetValueOrDefault("users/replaced-id") == 2 && archived.GetValueOrDefault("tokens/replaced-id") == 4
            && archived.GetValueOrDefault("administrators/replaced-id") == 1 && archived.GetValueOrDefault("usage_prices/replaced-id") == 4
            && !archived.Keys.Any(key => key.EndsWith("/orphan-reference", StringComparison.Ordinal)), string.Join(",", archived));
        var sentence = process.Get<IRelayInstance>().LastUpgrade!.Sentence();
        Check(sentence.Contains($"从版本 9 升到 {RelayDatabase.CurrentVersion}") && sentence.Contains("版本 10：使用者的旧文本编号 2 个") && sentence.Contains("个人令牌的旧文本编号 4 个"), sentence);
        // 升级前发出的东西照常可用：个人令牌、管理员会话、管理密码（旧哈希按旧主键算过，算法不读它）。
        foreach (var (name, token) in plain.Tokens)
        {
            var access = await process.Identities.Clients.Authenticate(token);
            Check(access?.UserId is { } id && (await process.Identities.List()).Single(user => user.Id == id).DisplayName == name, name + " token must still work");
        }
        Check(await process.Administrators.IsSessionValid(plain.Session), "The administrator session issued before the upgrade stays valid");
        Check(await process.Administrators.Login("fixture-v9-admin-password") is not null, "The old password hash still verifies");
        var saved = await process.Upstreams.ListCandidates();
        Check(saved.Select(item => item.Uid).SequenceEqual([plain.Upstreams["kept"], plain.Upstreams["standby"], plain.Upstreams["official"]]), "Order and uids survive; the deleted one stays deleted");
        var official = saved.Single(item => item.Uid == plain.Upstreams["official"]);
        Check(official.Options.OfficialLogin && process.Get<IOfficialAccountLogin>().HasCredential(official.Uid), "The imported official account still reads its credential");
        Check(Directory.Exists(Path.Combine(home.Path, ".state", "official-accounts", official.Uid)));
    }

    /// <summary>网页走一遍：建站即把真实 v9 库升到 10；用升级前发出的个人令牌经中转真实转发一次（本机模拟上游），用量记在原来那个使用者名下，个人用量接口只读出自己的。</summary>
    public static async Task RealVersion9Relay()
    {
        var plain = Plain();
        await using var mock = await UpstreamFixture.Create();
        await using var web = await WebFixture.Create(prepare: home => DatabaseUpgradeTests.CopyLegacy("v9", home));
        Check(web.App.Services.GetRequiredService<IRelayInstance>().LastUpgrade is { FromVersion: 9, ToVersion: RelayDatabase.CurrentVersion });
        await mock.Activate(web.Upstreams);
        var token = plain.Tokens["夹具甲"]; var self = (await web.Identities.Clients.Authenticate(token))!.UserId!.Value;
        using (var relayed = await UpstreamFixture.RelayRequest(web, token)) Check(relayed.StatusCode == HttpStatusCode.OK, "Relay with a pre-upgrade token: " + relayed.StatusCode);
        var personal = await UsageFixture.Personal(web, token);
        Check(personal.Rows.Count > 0 && personal.Rows.All(row => row.UserId == self && row.DisplayName == "夹具甲"), "Personal usage reads only the token owner");
        using var connection = web.Database.Open();
        Check(Convert.ToInt64(RelayDatabase.Scalar(connection, null, "SELECT user_id FROM usage_requests ORDER BY id DESC LIMIT 1")) == self);
    }

    /// <summary>bb20b32 程序导出的上游文件导入到全新的版本 10 实例：两家上游按文件顺序、名称和地址逐个对上，主键是库生成的整数，各有新的对外编号。</summary>
    public static async Task Version9ExportImports()
    {
        var plain = Plain();
        var file = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "LegacyDatabases", "v9", "upstreams.export"));
        await using var web = await WebFixture.Create();
        var outcomes = await web.Upstreams.Import((await web.Upstreams.ReadActive()).Version, file, plain.ExportPassword, CancellationToken.None);
        Check(outcomes.Select(item => (item.Name, item.Error)).SequenceEqual([("夹具甲供应商", (string?)null), ("夹具乙供应商", null)]), string.Join(" | ", outcomes));
        var saved = await web.Upstreams.ListCandidates();
        Check(saved.Select(item => (item.Name, item.Options.BaseUrl)).SequenceEqual([("夹具甲供应商", "https://relay-a.example.com"), ("夹具乙供应商", "https://relay-b.example.com")]));
        Check(saved.Select(item => item.Id).SequenceEqual(outcomes.Select(item => item.UpstreamCandidateId!.Value)) && saved.All(item => item.Id > 0 && Regex.IsMatch(item.Uid, "^[0-9a-f]{32}$")));
        Check(!saved.Any(item => plain.Upstreams.ContainsValue(item.Uid)), "Imported upstreams get new uids, not the source instance's");
    }

    /// <summary>升级到 10 在提交前失败：整体回滚，库仍是版本 9、文本主键、各表条数不变，备份已做好；去掉故障后照常升到 10。</summary>
    public static Task RollbackKeepsVersion9()
    {
        using var home = DatabaseUpgradeTests.Legacy("v9"); var path = DatabaseUpgradeTests.Database(home.Path);
        var rows = Rows(path); var rejected = false;
        try { _ = new TestProcess(home.Path, migrationCheckpoint: () => throw new IOException("fixture-v10-checkpoint")).Database; }
        // v9 夹具只剩第 10 版要做，检查点就在它提交前；接住它就是要验证的整体回滚。
        catch (IOException error) when (error.Message == "fixture-v10-checkpoint") { rejected = true; }
        Check(rejected && Version(path) == 9 && Rows(path).SequenceEqual(rows));
        using (var connection = DatabaseUpgradeTests.Connect(path))
            Check((string)RelayDatabase.Scalar(connection, null, "SELECT type FROM pragma_table_info('usage_requests') WHERE pk=1")! == "TEXT"
                && Convert.ToInt32(RelayDatabase.Scalar(connection, null, "SELECT COUNT(*) FROM sqlite_master WHERE name LIKE '%_v10'")) == 0, "Nothing of version 10 may remain");
        Check(Directory.GetFiles(Path.Combine(home.Path, ".state", "upgrade-backups"), "relay.db", SearchOption.AllDirectories).Length == 1);
        _ = new TestProcess(home.Path).Database;
        Check(Version(path) == RelayDatabase.CurrentVersion && Rows(path).SequenceEqual(rows));
        return Task.CompletedTask;
    }

    /// <summary>
    /// 非空引用对不上（库已损坏）时不猜：两张已轮换的旧令牌指向不存在的使用者，升级拒绝、读出哪张表几条，整体回滚仍是版本 9。
    /// 可空的上游引用对不上时照版本 9 的做法：原值另存、引用置空，记录本身保留。两种损坏都只在不开外键检查的直连上改数据行造出来。
    /// </summary>
    public static Task BrokenReferences()
    {
        using (var home = DatabaseUpgradeTests.Legacy("v9"))
        {
            var path = DatabaseUpgradeTests.Database(home.Path);
            using (var connection = DatabaseUpgradeTests.Connect(path)) RelayDatabase.Execute(connection, null, "PRAGMA foreign_keys=OFF; UPDATE tokens SET user_id='ghost-'||id WHERE revoked_at IS NOT NULL");
            var message = "";
            try { _ = new TestProcess(home.Path).Database; }
            // 按设计拒绝升级并读出损坏位置；接住它就是要验证的结果。
            catch (InvalidOperationException error) { message = error.Message; }
            Check(message.Contains("tokens 表有 2 条记录引用的 users 不存在") && message.Contains("已整体回滚") && Version(path) == 9, message);
        }
        using (var home = DatabaseUpgradeTests.Legacy("v9"))
        {
            var path = DatabaseUpgradeTests.Database(home.Path); string request;
            using (var connection = DatabaseUpgradeTests.Connect(path))
            {
                request = (string)RelayDatabase.Scalar(connection, null, "SELECT request_id FROM usage_attempts WHERE attempt=2")!;
                RelayDatabase.Execute(connection, null, "PRAGMA foreign_keys=OFF; UPDATE usage_attempts SET candidate_id='ghost-upstream' WHERE attempt=2");
            }
            _ = new TestProcess(home.Path).Database;
            Check(Version(path) == RelayDatabase.CurrentVersion && Archived(path).GetValueOrDefault("usage_attempts/orphan-reference") == 1);
            using var check = DatabaseUpgradeTests.Connect(path);
            Check(((string)RelayDatabase.Scalar(check, null, "SELECT row_json FROM migration_archive WHERE reason='orphan-reference'")!).Contains("ghost-upstream"));
            Check(RelayDatabase.Scalar(check, null, "SELECT a.candidate_id FROM usage_attempts a JOIN usage_requests r ON r.id=a.usage_request_id WHERE r.request_id=$id AND a.attempt=2", ("$id", request)) is null
                && Rows(path)["usage_attempts"] == 7, "The dangling reference is cleared and the attempt itself kept");
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// 新行的主键由库生成：使用者、上游按插入先后得到 1、2；使用者默认名仍是「u_」加 24 位随机小写十六进制，与主键无关；上游各有 32 位小写十六进制的对外编号；
    /// 同一上游同一模型再存一次单价只换费率、主键不变；会话按凭据哈希存；尝试记录按整数外键挂在它那条用量下。
    /// </summary>
    public static async Task NewRowsGetDatabaseKeys()
    {
        using var home = new TestHome(); var process = new TestProcess(home.Path);
        var first = await process.Identities.Create("甲"); var second = await process.Identities.Create("乙");
        Check(first.UserId == 1 && second.UserId == 2);
        Check((await process.Identities.List()).All(user => Regex.IsMatch(user.Name, "^u_[0-9a-f]{24}$")) && (await process.Identities.List())[0].Name != (await process.Identities.List())[1].Name);
        var version = (await process.Upstreams.ReadActive()).Version;
        var a = await process.Upstreams.SaveCandidate(version, new UpstreamOptions { BaseUrl = "https://key-a.example.com" }, "sk-key-a-0001", "甲家");
        var b = await process.Upstreams.SaveCandidate(version, new UpstreamOptions { BaseUrl = "https://key-b.example.com" }, "sk-key-b-0002", "乙家");
        Check(a == 1 && b == 2);
        var saved = await process.Upstreams.ListCandidates();
        Check(saved.All(item => Regex.IsMatch(item.Uid, "^[0-9a-f]{32}$")) && saved[0].Uid != saved[1].Uid);
        await process.Pricing.Save(a, "key-model", "USD", 1m, 2m, null, null);
        long PriceId() { using var connection = process.Database.Open(); return Convert.ToInt64(RelayDatabase.Scalar(connection, null, "SELECT id FROM usage_prices WHERE candidate_id=$a", ("$a", a))); }
        var priceId = PriceId(); await process.Pricing.Save(a, "key-model", "USD", 5m, 6m, null, null);
        Check(PriceId() == priceId && await process.Pricing.List(a) is [{ InputPerMillion: 5 }], "Overwriting a price keeps its key");
        await process.Administrators.Initialize((await process.Administrators.BootstrapCode())!, WebFixture.Password);
        var session = (await process.Administrators.Login(WebFixture.Password))!;
        using (var connection = process.Database.Open())
            Check((string?)RelayDatabase.Scalar(connection, null, "SELECT token_hash FROM admin_sessions WHERE admin_id=(SELECT id FROM administrators)") == ConfigurationFiles.Hash(session));
        var user = (await process.Identities.Clients.Authenticate(first.Token))!;
        await process.Usage.Start("key-request", user, "inference"); await process.Usage.Dispatch("key-request", version, a, "key-model");
        Check(await process.Usage.Finish("key-request", new UsageEnd("succeeded", true, 200, 200, 1, "complete", new(1, 2)) { Attempts = [new UpstreamAttempt(1, a, version, DateTimeOffset.UtcNow)] }));
        using (var connection = process.Database.Open())
        {
            Check(Convert.ToInt64(RelayDatabase.Scalar(connection, null, "SELECT a.usage_request_id FROM usage_attempts a")) == Convert.ToInt64(RelayDatabase.Scalar(connection, null, "SELECT id FROM usage_requests WHERE request_id='key-request'")));
            Check(Convert.ToInt64(RelayDatabase.Scalar(connection, null, "SELECT user_id FROM usage_requests")) == first.UserId && Convert.ToInt64(RelayDatabase.Scalar(connection, null, "SELECT candidate_id FROM usage_requests")) == a);
        }
        Check((await process.Usage.Query(new() { UserId = first.UserId })).Rows.Single().RequestId == "key-request" && (await process.Usage.Query(new() { UserId = second.UserId })).Total == 0);
        // 尝试记录随用量一起删（40 天清理按用量删）：经程序自己的连接（外键开着）删掉这条用量，它的尝试不能留下，也不能因外键拦住删除。
        using (var connection = process.Database.Open())
        {
            RelayDatabase.Execute(connection, null, "DELETE FROM usage_requests WHERE request_id='key-request'");
            Check(Convert.ToInt64(RelayDatabase.Scalar(connection, null, "SELECT COUNT(*) FROM usage_attempts")) == 0, "Attempts are deleted with their usage row");
        }
    }

    /// <summary>夹具 plain.json：升级前发出的个人令牌（显示名 → 明文）、管理员会话、导出密码，以及四家上游的旧文本主键。</summary>
    private sealed record PlainFixture(string Session, string ExportPassword, Dictionary<string, string> Tokens, Dictionary<string, string> Upstreams);

    /// <summary>读夹具里的明文假值。</summary>
    private static PlainFixture Plain() => JsonSerializer.Deserialize<PlainFixture>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "LegacyDatabases", "v9", "plain.json")),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    /// <summary>
    /// 量一份库里的六类关系，每条都写成一律用旧文本编号的元组：版本 9 直接读主键与外键列；版本 10 经整数外键连回上游的 uid、用量的 request_id、会话的 token_hash，
    /// 不留属性列的使用者、令牌、管理员按归档里 replaced-id 的旧编号与新号对回去。
    /// </summary>
    internal static Dictionary<string, HashSet<string>> Relations(string path)
    {
        using var connection = DatabaseUpgradeTests.Connect(path);
        var keyed = Convert.ToInt32(RelayDatabase.Scalar(connection, null, "PRAGMA user_version")) >= 10;
        string Old(string table, string column) => $"(SELECT json_extract(row_json,'$.id') FROM migration_archive WHERE source_table='{table}' AND reason='replaced-id' AND json_extract(row_json,'$.new_id')={column})";
        var queries = keyed ? new Dictionary<string, string>
        {
            ["usage"] = $"SELECT r.request_id||'|'||{Old("users", "r.user_id")}||'|'||{Old("tokens", "r.token_id")}||'|'||coalesce(c.uid,'') FROM usage_requests r LEFT JOIN upstream_candidates c ON c.id=r.candidate_id",
            ["attempts"] = "SELECT r.request_id||'|'||a.attempt||'|'||coalesce(c.uid,'') FROM usage_attempts a JOIN usage_requests r ON r.id=a.usage_request_id LEFT JOIN upstream_candidates c ON c.id=a.candidate_id",
            ["prices"] = "SELECT c.uid||'|'||p.model FROM usage_prices p JOIN upstream_candidates c ON c.id=p.candidate_id",
            ["tokens"] = $"SELECT {Old("tokens", "t.id")}||'|'||{Old("users", "t.user_id")}||'|'||t.version FROM tokens t",
            ["sessions"] = $"SELECT s.token_hash||'|'||{Old("administrators", "s.admin_id")} FROM admin_sessions s",
            ["active"] = "SELECT coalesce(c.uid,'') FROM upstream u LEFT JOIN upstream_candidates c ON c.id=u.candidate_id",
        } : new Dictionary<string, string>
        {
            ["usage"] = "SELECT request_id||'|'||user_id||'|'||token_id||'|'||coalesce(candidate_id,'') FROM usage_requests",
            ["attempts"] = "SELECT request_id||'|'||attempt||'|'||coalesce(candidate_id,'') FROM usage_attempts",
            ["prices"] = "SELECT candidate_id||'|'||model FROM usage_prices",
            ["tokens"] = "SELECT id||'|'||user_id||'|'||version FROM tokens",
            ["sessions"] = "SELECT id||'|'||admin_id FROM admin_sessions",
            ["active"] = "SELECT coalesce(candidate_id,'') FROM upstream",
        };
        var result = new Dictionary<string, HashSet<string>>();
        foreach (var (kind, sql) in queries)
        {
            var set = result[kind] = new HashSet<string>(StringComparer.Ordinal);
            using var command = RelayDatabase.Command(connection, null, sql);
            // 对不回去的一条会拼成 NULL：记成一个不可能与升级前相同的标记，集合比较必然不等。
            using var reader = command.ExecuteReader(); while (reader.Read()) set.Add(reader.IsDBNull(0) ? "<unmapped>" : reader.GetString(0));
        }
        return result;
    }

    /// <summary>各表行数。</summary>
    internal static Dictionary<string, long> Rows(string path)
    {
        using var connection = DatabaseUpgradeTests.Connect(path);
        return _tables.ToDictionary(table => table, table => Convert.ToInt64(RelayDatabase.Scalar(connection, null, $"SELECT COUNT(*) FROM {table}")));
    }

    /// <summary>库版本号。</summary>
    private static int Version(string path) { using var connection = DatabaseUpgradeTests.Connect(path); return Convert.ToInt32(RelayDatabase.Scalar(connection, null, "PRAGMA user_version")); }

    /// <summary>归档表按「原表/原因」汇总的条数；没有归档表时为空。</summary>
    private static Dictionary<string, long> Archived(string path)
    {
        using var connection = DatabaseUpgradeTests.Connect(path); var result = new Dictionary<string, long>();
        if (RelayDatabase.Scalar(connection, null, "SELECT 1 FROM sqlite_master WHERE name='migration_archive'") is null) return result;
        using var command = RelayDatabase.Command(connection, null, "SELECT source_table||'/'||reason,COUNT(*) FROM migration_archive GROUP BY 1");
        using var reader = command.ExecuteReader(); while (reader.Read()) result[reader.GetString(0)] = reader.GetInt64(1);
        return result;
    }
}
