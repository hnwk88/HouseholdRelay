using System.Text.Json;
using System.Text.Json.Nodes;
using Application.Interfaces.Configuration;
using Core.Configuration;
using HouseholdRelay.Composition;
using Infrastructure.Repositories.Configuration;
using Microsoft.Extensions.DependencyInjection;
using static Tests.TestSuite;

namespace Tests;

/// <summary>只在独立临时站点验证身份生成、损坏拒绝、普通写入和可恢复维护事务。</summary>
static class DeviceIdentityLifecycleTests
{
    /// <summary>八个并发首次打开只生成一次；重复读取、重启、普通保存和配套备份恢复保持同一个编号。</summary>
    public static async Task Persistence()
    {
        using var home = new TestHome(); var generator = new CountingGenerator();
        // 每个容器代表独立进程，首次建库的跨进程互斥必须先于随机生成。
        var opened = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            using var services = Services(home.Path, generator);
            return services.GetRequiredService<ISiteIdentityRepository>().Read();
        })));
        Check(generator.Count == 1 && opened.Select(item => item.DeviceId).Distinct().Count() == 1, "Concurrent creation generates exactly once");
        Check(opened.All(item => item.Version == 1 && !item.Pending && item.OperationId is null));
        using var process = Services(home.Path, generator);
        var settings = process.GetRequiredService<IConfigurationRepository>();
        for (var index = 0; index < 8; index++) Check(settings.Read().Value.DeviceId == opened[0].DeviceId);
        var saved = settings.Read(); saved.Value.MaxConcurrent = 3; settings.Save(saved.Version, saved.Value);
        using var restarted = Services(home.Path, generator);
        Check(restarted.GetRequiredService<ISiteIdentityRepository>().Read() == opened[0] && generator.Count == 1);
        using var restored = new TestHome(); Directory.CreateDirectory(Path.Combine(restored.Path, ".state"));
        File.Copy(Path.Combine(home.Path, ".relay-instance"), Path.Combine(restored.Path, ".relay-instance"));
        File.Copy(process.GetRequiredService<RelayDatabase>().Path, Path.Combine(restored.Path, ".state", "relay.db"));
        using var recovered = Services(restored.Path, generator);
        Check(recovered.GetRequiredService<ISiteIdentityRepository>().Read() == opened[0] && generator.Count == 1, "Paired backup preserves canonical identity");
    }

    /// <summary>缺失、null、空、空白、短值、非法字符和坏 JSON 均不能补号或重开初始化。</summary>
    public static Task Corruption()
    {
        foreach (var replacement in new[] { "missing", "null", "empty", "space", "short", "invalid", "json" })
        {
            using var legacy = new TestHome(); legacy.WriteLegacy(); var generator = new CountingGenerator();
            var path = Path.Combine(legacy.Path, ".state", "server.json");
            var broken = Corrupt(File.ReadAllText(path), replacement); File.WriteAllText(path, broken);
            using (var rejected = Services(legacy.Path, generator)) Refused(() => rejected.GetRequiredService<RelayDatabase>());
            Check(generator.Count == 0 && !File.Exists(Path.Combine(legacy.Path, ".relay-instance")) && File.ReadAllText(path) == broken,
                "Damaged legacy identity must not create a new station");
            using var home = new TestHome(); using var services = Services(home.Path, generator);
            var database = services.GetRequiredService<RelayDatabase>();
            var old = services.GetRequiredService<IConfigurationRepository>().Read();
            broken = Corrupt(JsonSerializer.Serialize(old.Value, ConfigurationFiles.Json), replacement);
            database.Write((connection, transaction) => RelayDatabase.Execute(connection, transaction, "UPDATE settings SET json=$json WHERE id=1", ("$json", broken)));
            Refused(() => services.GetRequiredService<IConfigurationRepository>().Read());
            using var reopened = Services(home.Path, generator); Refused(() => reopened.GetRequiredService<RelayDatabase>());
            Check(generator.Count == 1 && File.Exists(Path.Combine(home.Path, ".relay-instance")), "Corrupt existing station never regenerates");
        }
        using var valid = new TestHome(); var initial = valid.WriteLegacy(); var kept = new CountingGenerator();
        using var imported = Services(valid.Path, kept);
        Check(imported.GetRequiredService<ISiteIdentityRepository>().Read().DeviceId == initial.DeviceId && kept.Count == 0, "Legacy import generates no replacement");
        foreach (var asset in new[] { "official-accounts", "owned-claude-processes", "keys", "legacy-backups", "relay.db-wal", "relay.db-shm", "serving.lock" })
        {
            using var orphan = new TestHome(); var generator = new CountingGenerator();
            var state = Path.Combine(orphan.Path, ".state"); Directory.CreateDirectory(state);
            if (asset.Contains('.')) File.WriteAllText(Path.Combine(state, asset), "fixture orphan bytes");
            else { var directory = Path.Combine(state, asset); Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "fixture-marker"), "fixture orphan bytes"); }
            using var refused = Services(orphan.Path, generator); Refused(() => refused.GetRequiredService<RelayDatabase>());
            Check(generator.Count == 0 && !File.Exists(Path.Combine(orphan.Path, ".relay-instance")), "Residual station assets cannot reopen setup");
        }
        return Task.CompletedTask;
    }

    /// <summary>普通配置与两个证书仓储入口都拒绝改号，错误事务不写证书或递增设置版本。</summary>
    public static Task SaveProtection()
    {
        using var home = new TestHome(); using var services = Services(home.Path, new CountingGenerator());
        var settings = services.GetRequiredService<IConfigurationRepository>(); var before = settings.Read();
        var edited = settings.Read(); edited.Value.DeviceId = new string('e', 64);
        Refused(() => settings.Save(edited.Version, edited.Value));
        var certificates = services.GetRequiredService<ICertificateRepository>();
        Refused(() => certificates.SaveImported(edited.Version, edited.Value, [1, 2, 3], null!));
        Refused(() => certificates.SavePrivate(edited.Version, edited.Value, [1, 2, 3], null!, "fixture public certificate"));
        Check(settings.Read().Version == before.Version && settings.Read().Value.DeviceId == before.Value.DeviceId && certificates.ReadProtectedCertificate() is null);
        return Task.CompletedTask;
    }

    /// <summary>同操作八次并发只生成一次，pending 重启保留，历史完成重试不改变后续编号或个人令牌。</summary>
    public static async Task Maintenance()
    {
        using var home = new TestHome(); var generator = new CountingGenerator(); using var services = Services(home.Path, generator);
        var repository = services.GetRequiredService<ISiteIdentityRepository>(); var first = repository.Read();
        var identities = services.GetRequiredService<IIdentityService>();
        var issued = Enumerable.Range(0, 4).Select(index => identities.Create("独立电脑" + index)).ToArray();
        var beforeTokens = TokenRows(services.GetRequiredService<RelayDatabase>());
        var operation = Guid.NewGuid().ToString("N");
        // 仓储事务内的版本检查必须使同一操作的所有竞争者观察到同一持久结果。
        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => repository.Begin(operation, first.Version))));
        Check(generator.Count == 2 && attempts.All(item => item.Pending && item.Version == 2 && item.Current == attempts[0].Current));
        Check(attempts[0].Current.DeviceId != first.DeviceId);
        using var reopened = Services(home.Path, generator); var restart = reopened.GetRequiredService<ISiteIdentityRepository>();
        Check(restart.Read() == attempts[0].Current && restart.Begin(operation, 1).Current == attempts[0].Current && generator.Count == 2);
        Refused(() => repository.Begin(Guid.NewGuid().ToString("N"), 1));
        var completed = restart.Complete(operation); Check(!completed.Pending && !repository.Read().Pending);
        Check(repository.Begin(operation, 1).Version == 2 && generator.Count == 2);
        var nextOperation = Guid.NewGuid().ToString("N"); var second = repository.Begin(nextOperation, 2);
        var historical = repository.Begin(operation, 1);
        Check(historical.Version == 2 && !historical.Pending && historical.Current == second.Current && generator.Count == 3);
        repository.Complete(operation); Check(repository.Read() == second.Current, "An old completion cannot clear newer pending state");
        repository.Complete(nextOperation);
        Refused(() => repository.Begin(Guid.NewGuid().ToString("N"), 2));
        Check(generator.Count == 3 && repository.Read().Version == 3, "An old page cannot start another replacement after a newer operation is ready");
        // 两个不同操作争用同一旧代次，写锁必须只让其中一个提交，另一个不能生成。
        var beforeRace = generator.Count;
        var competition = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            try { return repository.Begin(Guid.NewGuid().ToString("N"), 3); }
            // 另一操作已提交 pending，败方的明确拒绝是预期竞争结果。
            catch (InvalidOperationException) { return null; }
        })));
        Check(competition.Count(item => item is not null) == 1 && generator.Count == beforeRace + 1);
        repository.Complete(competition.Single(item => item is not null)!.OperationId);
        Check(TokenRows(services.GetRequiredService<RelayDatabase>()) == beforeTokens && issued.All(item => identities.Clients.Authenticate(item.Token)?.Id == item.UserId));
    }

    /// <summary>事务提交前故障保持旧编号；提交后的 pending 与 ready 转换可经重启继续同一目标。</summary>
    public static async Task Recovery()
    {
        using var home = new TestHome(); var generator = new CountingGenerator(); using var services = Services(home.Path, generator);
        var database = services.GetRequiredService<RelayDatabase>(); var repository = services.GetRequiredService<ISiteIdentityRepository>();
        var before = repository.Read(); var operation = Guid.NewGuid().ToString("N");
        database.Write((connection, transaction) => RelayDatabase.Execute(connection, transaction,
            "CREATE TRIGGER reject_identity_commit BEFORE INSERT ON site_identity_operations BEGIN SELECT RAISE(ABORT,'fixture interrupted commit'); END"));
        var failed = false;
        try { repository.Begin(operation, 1); }
        // 模拟 SQLite 写事务提交之前失败，随后检查 canonical 和 pending 都没有部分提交。
        catch (Microsoft.Data.Sqlite.SqliteException) { failed = true; }
        Check(failed && repository.Read() == before);
        database.Write((connection, transaction) => RelayDatabase.Execute(connection, transaction, "DROP TRIGGER reject_identity_commit"));
        var pending = repository.Begin(operation, 1); var generated = generator.Count;
        using var reopened = Services(home.Path, generator); var resumed = reopened.GetRequiredService<ISiteIdentityRepository>();
        Check(resumed.Read() == pending.Current && resumed.Begin(operation, 1).Current == pending.Current && generator.Count == generated);
        resumed.Complete(operation); using var ready = Services(home.Path, generator);
        Check(ready.GetRequiredService<ISiteIdentityRepository>().Read() == pending.Current with { Pending = false } && generator.Count == generated);
        database.Write((connection, transaction) => RelayDatabase.Execute(connection, transaction,
            "DROP TABLE site_identity; DROP TABLE site_identity_operations; PRAGMA user_version=7;"));
        using var migrationFailure = Services(home.Path, generator, () => throw new IOException("fixture migration interruption"));
        failed = false;
        try { _ = migrationFailure.GetRequiredService<RelayDatabase>(); }
        // 仅用于迁移回滚的受控异常；重试必须保留已经写入 settings 的原编号。
        catch (IOException) { failed = true; }
        using (var connection = database.Open()) Check(failed && Convert.ToInt32(RelayDatabase.Scalar(connection, null, "PRAGMA user_version")) == 7);
        using var migrationRetry = Services(home.Path, generator);
        Check(migrationRetry.GetRequiredService<ISiteIdentityRepository>().Read().DeviceId == pending.Current.DeviceId && generator.Count == generated);
        var recoveredRepository = migrationRetry.GetRequiredService<ISiteIdentityRepository>();
        var recoveredIdentity = recoveredRepository.Read();
        using (var blockingConnection = database.Open())
        using (var blockingTransaction = blockingConnection.BeginTransaction())
        {
            var elapsed = System.Diagnostics.Stopwatch.StartNew(); var refused = false;
            try { recoveredRepository.Begin(Guid.NewGuid().ToString("N"), recoveredIdentity.Version); }
            // 真实写锁仍被另一连接持有时必须立即固定拒绝，不能等provider隐式重试后才继续。
            catch (InvalidOperationException error) { refused = error.Message == "站点配置正由另一项操作保存，设备编号未更换。请稍后继续原操作。"; }
            elapsed.Stop();
            Check(refused && elapsed.Elapsed < TimeSpan.FromSeconds(3) && recoveredRepository.Read() == recoveredIdentity && generator.Count == generated,
                "Busy SQLite must refuse within three seconds without generating or committing");
            blockingTransaction.Rollback();
        }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); var cancellationObserved = false;
        try { await Task.Run(() => recoveredRepository.Begin(Guid.NewGuid().ToString("N"), recoveredIdentity.Version, cancelled.Token)); }
        // 取消独立于数据库忙处理，仓储排队前就停止，不能借已释放的写锁再生成。
        catch (OperationCanceledException) { cancellationObserved = true; }
        Check(cancellationObserved && recoveredRepository.Read() == recoveredIdentity && generator.Count == generated, "A cancelled maintenance cannot generate or commit after the writer is free");
        database.Write((connection, transaction) => RelayDatabase.Execute(connection, transaction, "DELETE FROM site_identity"));
        using var missingState = Services(home.Path, generator); Refused(() => missingState.GetRequiredService<RelayDatabase>());
        Check(generator.Count == generated, "An upgraded database with missing identity state must not repair itself to ready");
    }

    /// <summary>以真实组合根加计数生成器建立测试进程，不接触正式数据或上游。</summary>
    private static ServiceProvider Services(string home, CountingGenerator generator, Action? checkpoint = null)
    {
        var services = new ServiceCollection().AddHouseholdRelay(options => { options.Home = home; options.MigrationCheckpoint = checkpoint; });
        services.AddSingleton<IDeviceIdGenerator>(generator);
        return services.BuildServiceProvider();
    }

    /// <summary>构造七类损坏配置，不展示内容或原编号。</summary>
    private static string Corrupt(string json, string replacement)
    {
        if (replacement == "json") return "{invalid fixture";
        var value = JsonNode.Parse(json)!.AsObject();
        if (replacement == "missing") value.Remove("deviceId");
        else value["deviceId"] = replacement switch { "null" => null, "empty" => "", "space" => "  ", "short" => new string('a', 63), _ => new string('g', 64) };
        return value.ToJsonString();
    }

    /// <summary>固定字段快照证明站点维护没有改动个人令牌的任何持久属性。</summary>
    private static string TokenRows(RelayDatabase database)
    {
        using var connection = database.Open();
        using var command = RelayDatabase.Command(connection, null, "SELECT id,user_id,version,hash,coalesce(revoked_at,'') FROM tokens ORDER BY id");
        using var reader = command.ExecuteReader(); var values = new List<string>();
        while (reader.Read()) values.Add(string.Join('|', Enumerable.Range(0, 5).Select(reader.GetValue)));
        return string.Join('\n', values);
    }

    /// <summary>预期业务拒绝；未抛出时用断言失败指出边界被绕过。</summary>
    private static void Refused(Action action)
    {
        var refused = false;
        try { action(); }
        // 损坏输入和改号请求必须以明确业务错误拒绝，不把未知异常当作通过。
        catch (InvalidOperationException) { refused = true; }
        Check(refused, "Expected an explicit identity refusal");
    }

    /// <summary>只用于测试的唯一生成器，计数可证明竞争和重启没有额外生成。</summary>
    private sealed class CountingGenerator : IDeviceIdGenerator
    {
        private int count;
        /// <summary>已执行的生成次数。</summary>
        public int Count => Volatile.Read(ref count);
        /// <summary>线程安全地产生格式合法且逐次不同的假编号。</summary>
        public string Generate() => Interlocked.Increment(ref count).ToString("x64");
    }
}
