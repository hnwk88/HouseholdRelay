using System.Diagnostics;
using System.Net;
using HouseholdRelay;
using Microsoft.Data.Sqlite;
using static Tests;
using static UsageFixture;

static class UsageStorageTests
{
    public static async Task WriteFailures()
    {
        await using var upstream = await UpstreamFixture.Create(); await using var web = await WebFixture.Create();
        var user = web.Identities.Create("存储失败"); await upstream.Activate(new(web.Database)); var calls = upstream.Calls.Count;
        using (var connection = web.Database.Open())
        using (var locked = connection.BeginTransaction())
        {
            Check(await Call(web, user.Token) == HttpStatusCode.ServiceUnavailable);
            Check(upstream.Calls.Count == calls && web.Relay.Usage!.Query(new()).Total == 0, "Failed start must not send or invent a record");
        }
        var client = web.Identities.Clients.Authenticate(user.Token)!; var usage = web.Relay.Usage!;
        var completed = new UsageEnd("succeeded", true, 200, 200, 1, "complete", new(12, 9));
        await usage.Start("finish-retry", client, "inference"); await usage.Dispatch("finish-retry", 2, "fixture-model");
        Task<bool> finish;
        using (var connection = web.Database.Open())
        using (var locked = connection.BeginTransaction())
        {
            finish = Task.Run(() => usage.Finish("finish-retry", completed));
            await Task.Delay(1200); // 首次忙等待失败，下一次独立保存仍应成功。
        }
        Check(await finish.WaitAsync(TimeSpan.FromSeconds(5)));
        Check(usage.Query(new()).Rows.Single().Completeness == "complete");
        await usage.Start("finish-failed", client, "inference");
        web.Database.Write((connection, transaction) => RelayDatabase.Execute(connection, transaction, """
            CREATE TRIGGER reject_usage_finish BEFORE UPDATE ON usage_requests BEGIN SELECT RAISE(ABORT,'fixture-write-failure'); END
            """));
        var timer = Stopwatch.StartNew(); Check(!await usage.Finish("finish-failed", completed));
        Check(timer.Elapsed < TimeSpan.FromSeconds(5) && usage.PersistenceFailures == 2);
        Check(usage.Query(new()).Rows.Single(row => row.RequestId == "finish-failed").Outcome == "started");
        await web.Login(); Check((await web.Http.GetStringAsync(web.Url + "/Usage")).Contains("待写入记录不能视为完整统计"));
        web.Database.Write((connection, transaction) => RelayDatabase.Execute(connection, transaction, "DROP TRIGGER reject_usage_finish"));
        usage.Recover(); var restored = usage.Query(new()).Rows.Single(row => row.RequestId == "finish-failed");
        Check(restored.Outcome == "interrupted" && restored.Completeness == "partial" && restored.Tokens.Input is null);
        web.Database.Write((connection, transaction) => RelayDatabase.Execute(connection, transaction, """
            CREATE TRIGGER reject_usage_start BEFORE INSERT ON usage_requests BEGIN SELECT RAISE(ABORT,'fixture-readonly'); END
            """));
        Check(await Call(web, user.Token) == HttpStatusCode.ServiceUnavailable && upstream.Calls.Count == calls);
    }

    public static async Task RetentionAndRecovery()
    {
        using var home = new TestHome(); var database = new RelayDatabase(home.Path); var identities = new IdentityService(database);
        var token = identities.Create("保留窗口"); var user = identities.Clients.Authenticate(token.Token)!;
        var now = new DateTimeOffset(2026, 11, 2, 6, 30, 0, TimeSpan.Zero); var clock = new UsageClock(now); var usage = new UsageStore(database, clock);
        var end = new UsageEnd("succeeded", true, 200, 200, 1, "complete", new(2, 3));
        foreach (var (id, time) in new[] { ("expired", now.AddDays(-40).AddTicks(-1)), ("boundary", now.AddDays(-40)), ("inside", now.AddDays(-40).AddTicks(1)), ("recent", now.AddHours(-1)) })
        { clock.Set(time); await usage.Start(id, user, "inference"); await usage.Finish(id, end); }
        clock.Set(now); await usage.Start("unfinished", user, "inference"); await usage.Dispatch("unfinished", 1, "fixture-model");
        Check(usage.Query(new()).Total == 4, "Queries must filter expired data before deletion runs");
        var reopened = new RelayDatabase(home.Path); var resumed = new UsageStore(reopened, clock);
        Check(resumed.Query(new()).Rows.Single(row => row.RequestId == "unfinished").Outcome == "started", "Opening for queries must not recover live requests");
        using (var maintenance = new UsageMaintenance(resumed))
        {
            await maintenance.StartAsync(CancellationToken.None);
            await Until(() => clock.TimerCount > 0);
            using (var duplicate = new UsageMaintenance(new UsageStore(reopened, clock)))
            {
                var rejected = false; try { await duplicate.StartAsync(CancellationToken.None); } catch (IOException) { rejected = true; }
                Check(rejected, "A second server must not recover the first server's active records");
            }
            var report = resumed.Query(new()); Check(report.Total == 4);
            Check(report.Rows.Single(row => row.RequestId == "unfinished").Outcome == "interrupted");
            Check(report.Rows.Single(row => row.RequestId == "boundary").Tokens == new UsageTokens(2, 3));
            using (var connection = reopened.Open()) Check(Convert.ToInt32(RelayDatabase.Scalar(connection, null, "SELECT COUNT(*) FROM usage_requests")) == 4);
            // 模拟跨过小时和夏令时回拨后的运行期；时区表示不改变 UTC 截止点。
            clock.Advance(TimeSpan.FromHours(1));
            await Until(() => { using var connection = reopened.Open(); return Convert.ToInt32(RelayDatabase.Scalar(connection, null, "SELECT COUNT(*) FROM usage_requests")) == 2; });
            Check(resumed.Query(new(From: now.AddDays(-40).ToOffset(TimeSpan.FromHours(-5)))).Total == 2);
            await maintenance.StopAsync(CancellationToken.None);
        }
        clock.Advance(TimeSpan.FromDays(41));
        using (var startup = new UsageMaintenance(new UsageStore(new RelayDatabase(home.Path), clock)))
        { await startup.StartAsync(CancellationToken.None); Check(resumed.Query(new()).Total == 0); await startup.StopAsync(CancellationToken.None); }
        Check(new IdentityService(reopened).Clients.Authenticate(token.Token)?.Id == user.Id && new ConfigurationService(reopened).Read().Version == 1);
    }

    public static Task MigrationRollback()
    {
        using var home = new TestHome(); var database = new RelayDatabase(home.Path); var identities = new IdentityService(database); var user = identities.Create("迁移");
        database.Write((connection, transaction) => RelayDatabase.Execute(connection, transaction, """
            DROP TABLE usage_requests; DROP TABLE usage_prices; PRAGMA user_version=2;
            CREATE TABLE usage_requests(unexpected TEXT);
            """));
        var rejected = false; try { _ = new RelayDatabase(home.Path); } catch (SqliteException) { rejected = true; }
        Check(rejected);
        using (var connection = database.Open())
        { Check(Convert.ToInt32(RelayDatabase.Scalar(connection, null, "PRAGMA user_version")) == 2); Check(Convert.ToInt32(RelayDatabase.Scalar(connection, null, "SELECT COUNT(*) FROM sqlite_master WHERE name='usage_time'")) == 0); }
        database.Write((connection, transaction) => RelayDatabase.Execute(connection, transaction, "DROP TABLE usage_requests"));
        var upgraded = new RelayDatabase(home.Path); Check(new IdentityService(upgraded).Clients.Authenticate(user.Token)?.Id == user.UserId);
        Check(new UsageStore(upgraded).Query(new()).Total == 0);
        return Task.CompletedTask;
    }
}

/// <summary>固定 UTC 时钟同时驱动维护计时器，无需等待真实一小时或四十天。</summary>
sealed class UsageClock(DateTimeOffset initial) : TimeProvider
{
    private long ticks = initial.UtcTicks;
    private readonly object sync = new();
    private readonly List<UsageTimer> timers = [];
    public int TimerCount { get { lock (sync) return timers.Count; } }
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
    public void Set(DateTimeOffset value) => Interlocked.Exchange(ref ticks, value.UtcTicks);
    public void Advance(TimeSpan duration)
    { Interlocked.Add(ref ticks, duration.Ticks); UsageTimer[] snapshot; lock (sync) snapshot = timers.ToArray(); foreach (var timer in snapshot) timer.Tick(GetUtcNow()); }
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    { var timer = new UsageTimer(this, callback, state, dueTime, period); lock (sync) timers.Add(timer); return timer; }
    private sealed class UsageTimer(UsageClock clock, TimerCallback callback, object? state, TimeSpan due, TimeSpan period) : ITimer
    {
        private DateTimeOffset next = clock.GetUtcNow() + due;
        private TimeSpan interval = period;
        private bool disposed;
        public bool Change(TimeSpan dueTime, TimeSpan period)
        { next = clock.GetUtcNow() + dueTime; interval = period; return !disposed; }
        public void Tick(DateTimeOffset time)
        { if (disposed || time < next) return; next = time + interval; callback(state); }
        public void Dispose() => disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
