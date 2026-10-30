using System.Diagnostics;
using System.Net;
using HouseholdRelay;
using Microsoft.Data.Sqlite;
using static Tests.TestSuite;
using static Tests.UsageFixture;

namespace Tests;

static class UsageStorageTests
{
    /// <summary>
    /// 验证用量写入繁忙或失败时的处理：数据库被写事务占住时新请求返回 503，既不发往上游也不凭空生成记录；
    /// 完成记录首次遇忙后重试成功；完成写入被触发器拒绝时 5 秒内返回失败、失败计数为 2、记录停在已开始，
    /// 管理页提示待写入记录不能视为完整统计；恢复后该记录标为中断且部分完整、输入令牌未知；
    /// 开始记录无法写入时请求返回 503 且不发往上游。
    /// </summary>
    public static async Task WriteFailures()
    {
        await using var upstream = await UpstreamFixture.Create(); await using var web = await WebFixture.Create();
        var user = web.Identities.Create("存储失败"); await upstream.Activate(web.Upstreams); var calls = upstream.Calls.Count;
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

    /// <summary>
    /// 验证 UTC 保留边界、每小时清理和重启恢复（保留 40 天）：超出 40 天哪怕一个刻度的记录在查询时就被过滤，恰好 40 天的仍保留；
    /// 仅为查询打开数据库不会把进行中的记录判为中断；维护服务启动时恢复中断记录并删除过期数据，第二个实例启动被拒绝；
    /// 虚拟时钟前进一小时后按 UTC 截止点再清理，换时区查询结果一致；41 天后再启动全部清空，身份和设置不受影响。
    /// </summary>
    public static async Task RetentionAndRecovery()
    {
        using var home = new TestHome(); var process = new TestProcess(home.Path); var database = process.Database; var identities = process.Identities;
        var token = identities.Create("保留窗口"); var user = identities.Clients.Authenticate(token.Token)!;
        var now = new DateTimeOffset(2026, 11, 2, 6, 30, 0, TimeSpan.Zero); var clock = new UsageClock(now); var usage = new TestProcess(home.Path, clock).Usage;
        var end = new UsageEnd("succeeded", true, 200, 200, 1, "complete", new(2, 3));
        foreach (var (id, time) in new[] { ("expired", now.AddDays(-40).AddTicks(-1)), ("boundary", now.AddDays(-40)), ("inside", now.AddDays(-40).AddTicks(1)), ("recent", now.AddHours(-1)) })
        { clock.Set(time); await usage.Start(id, user, "inference"); await usage.Finish(id, end); }
        clock.Set(now); await usage.Start("unfinished", user, "inference"); await usage.Dispatch("unfinished", 1, "fixture-model");
        Check(usage.Query(new()).Total == 4, "Queries must filter expired data before deletion runs");
        var reopenedProcess = new TestProcess(home.Path, clock); var reopened = reopenedProcess.Database; var resumed = reopenedProcess.Usage;
        Check(resumed.Query(new()).Rows.Single(row => row.RequestId == "unfinished").Outcome == "started", "Opening for queries must not recover live requests");
        using (var maintenance = reopenedProcess.Get<UsageMaintenance>())
        {
            await maintenance.StartAsync(CancellationToken.None);
            await Until(() => clock.TimerCount > 0);
            using (var duplicate = new TestProcess(home.Path, clock).Get<UsageMaintenance>())
            {
                var rejected = false; try { await duplicate.StartAsync(CancellationToken.None); } catch (IOException) { /* 同一数据目录只允许一个维护实例，第二个必须拒绝启动，以免它把第一个实例的进行中记录误判为中断。 */ rejected = true; }
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
        using (var startup = new TestProcess(home.Path, clock).Get<UsageMaintenance>())
        { await startup.StartAsync(CancellationToken.None); Check(resumed.Query(new()).Total == 0); await startup.StopAsync(CancellationToken.None); }
        Check(reopenedProcess.Identities.Clients.Authenticate(token.Token)?.Id == user.Id && reopenedProcess.Settings.Read().Version == 1);
    }

    /// <summary>
    /// 验证用量表结构升级在保住身份前回滚：预置冲突的 usage_requests 表时升级失败，版本号仍为 2 且未建出时间索引；
    /// 去掉冲突后升级成功，成员令牌仍对应原成员，用量为空。
    /// </summary>
    public static Task MigrationRollback()
    {
        using var home = new TestHome(); var process = new TestProcess(home.Path); var database = process.Database; var identities = process.Identities; var user = identities.Create("迁移");
        database.Write((connection, transaction) => RelayDatabase.Execute(connection, transaction, """
            DROP TABLE usage_attempts; DROP TABLE usage_requests; DROP TABLE usage_prices;
            ALTER TABLE upstream_candidates DROP COLUMN name; ALTER TABLE upstream DROP COLUMN candidate_id; PRAGMA user_version=2;
            CREATE TABLE usage_requests(unexpected TEXT);
            """));
        var rejected = false; try { _ = new TestProcess(home.Path).Database; } catch (SqliteException) { /* 冲突的表结构让升级中途失败，预期以 SQLite 异常整体回滚；随后核对版本号和索引未变。 */ rejected = true; }
        Check(rejected);
        using (var connection = database.Open())
        { Check(Convert.ToInt32(RelayDatabase.Scalar(connection, null, "PRAGMA user_version")) == 2); Check(Convert.ToInt32(RelayDatabase.Scalar(connection, null, "SELECT COUNT(*) FROM sqlite_master WHERE name='usage_time'")) == 0); }
        database.Write((connection, transaction) => RelayDatabase.Execute(connection, transaction, "DROP TABLE usage_requests"));
        var upgraded = new TestProcess(home.Path); Check(upgraded.Identities.Clients.Authenticate(user.Token)?.Id == user.UserId);
        Check(upgraded.Usage.Query(new()).Total == 0);
        return Task.CompletedTask;
    }
}

/// <summary>固定 UTC 时钟同时驱动维护计时器，无需等待真实一小时或四十天。</summary>
sealed class UsageClock(DateTimeOffset initial) : TimeProvider
{
    private long ticks = initial.UtcTicks;
    private readonly object sync = new();
    private readonly List<UsageTimer> timers = [];
    /// <summary>已创建的计时器数量，测试据此确认维护服务已挂上定时清理。</summary>
    // 计时器列表会被维护服务和测试线程同时访问，读取数量也要加锁。
    public int TimerCount { get { lock (sync) return timers.Count; } }
    /// <summary>返回测试设定的当前 UTC 时间。</summary>
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
    /// <summary>直接把时钟拨到指定时刻，不触发计时器，用于在不同时间点写入记录。</summary>
    public void Set(DateTimeOffset value) => Interlocked.Exchange(ref ticks, value.UtcTicks);
    /// <summary>把时钟向前推进指定时长，并让所有已到期的计时器立即回调，模拟时间流逝后的定时维护。</summary>
    public void Advance(TimeSpan duration)
    // 在锁内只拷贝计时器快照、在锁外回调，避免回调中新建计时器时修改正在遍历的列表或在锁内执行耗时维护。
    { Interlocked.Add(ref ticks, duration.Ticks); UsageTimer[] snapshot; lock (sync) snapshot = timers.ToArray(); foreach (var timer in snapshot) timer.Tick(GetUtcNow()); }
    /// <summary>创建受本时钟驱动的假计时器并登记，供 <see cref="Advance"/> 检查是否到期。</summary>
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    // 登记计时器可能与 Advance 取快照同时发生，用同一把锁保护列表。
    { var timer = new UsageTimer(this, callback, state, dueTime, period); lock (sync) timers.Add(timer); return timer; }
    /// <summary>只在 <see cref="Advance"/> 时检查到期的假计时器：到期就回调一次，并按周期排定下一次。</summary>
    private sealed class UsageTimer(UsageClock clock, TimerCallback callback, object? state, TimeSpan due, TimeSpan period) : ITimer
    {
        private DateTimeOffset next = clock.GetUtcNow() + due;
        private TimeSpan interval = period;
        private bool disposed;
        /// <summary>按当前虚拟时间重新设定下次到期时间和周期；已释放时返回 false。</summary>
        public bool Change(TimeSpan dueTime, TimeSpan period)
        { next = clock.GetUtcNow() + dueTime; interval = period; return !disposed; }
        /// <summary>未释放且虚拟时间已到达下次到期时间时，先按周期推迟下次到期再执行回调。</summary>
        public void Tick(DateTimeOffset time)
        { if (disposed || time < next) return; next = time + interval; callback(state); }
        /// <summary>标记为已释放，之后不再回调。</summary>
        public void Dispose() => disposed = true;
        /// <summary>异步释放，行为与 <see cref="Dispose"/> 相同。</summary>
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
