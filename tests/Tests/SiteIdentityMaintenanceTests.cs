using Application.Interfaces.ClaudeCode;
using Application.Interfaces.Configuration;
using HouseholdRelay.Composition;
using Microsoft.Extensions.DependencyInjection;
using static Tests.TestSuite;

namespace Tests;

/// <summary>以真实持久仓储、假时钟和受控准备器验证官方操作排空与维护失败恢复。</summary>
static class SiteIdentityMaintenanceTests
{
    /// <summary>在途许可未归还不提交；八次同操作只换一次，关闭期间八次新官方准入均失败。</summary>
    public static async Task DrainAndConcurrency()
    {
        using var fixture = new MaintenanceFixture(); await fixture.Service.Prepare(default);
        var before = fixture.Service.Current; var permit = fixture.Service.Enter();
        var operation = Guid.NewGuid().ToString("N");
        var changing = fixture.Service.Refresh(operation, before.Version, default);
        Check(!changing.IsCompleted && !fixture.Service.Available && fixture.Repository.Read() == before);
        for (var index = 0; index < 8; index++) Denied(fixture.Service);
        // 其余并发维护等同一互斥；模拟完整 SSE 释放许可之前任何一项都不能提交。
        var duplicates = Enumerable.Range(0, 7).Select(_ => fixture.Service.Refresh(operation, before.Version, default)).ToArray();
        Check(duplicates.All(item => !item.IsCompleted) && fixture.Generator.Count == 1);
        permit.Dispose(); permit.Dispose();
        // 排空后收集全部并发结果，不能只断言第一个成功而漏掉重复提交失败。
        var results = await Task.WhenAll(duplicates.Append(changing));
        Check(fixture.Generator.Count == 2 && results.All(item => item.Version == 2 && !item.Pending));
        Check(fixture.Service.Available && fixture.Service.Current.DeviceId != before.DeviceId);
        using var continued = fixture.Service.Enter();
        var repeated = fixture.Service.Refresh(operation, before.Version, default);
        Check(repeated.IsCompletedSuccessfully && !(await repeated).Pending && fixture.Service.Available,
            "A completed operation retry must return immediately while a newer official operation is active");
        var resumed = await fixture.Service.Resume(operation, default);
        Check(!resumed.Pending && fixture.Service.Available, "Completed resume must not wait on or modify live operations");
    }

    /// <summary>60 秒包括等待维护锁；超时不提交、不杀在途操作，旧身份恢复准入。</summary>
    public static async Task DrainTimeout()
    {
        using var fixture = new MaintenanceFixture(); await fixture.Service.Prepare(default);
        var before = fixture.Service.Current; using var permit = fixture.Service.Enter();
        var first = fixture.Service.Refresh(Guid.NewGuid().ToString("N"), before.Version, default);
        var second = fixture.Service.Refresh(Guid.NewGuid().ToString("N"), before.Version, default);
        fixture.Clock.Advance(TimeSpan.FromSeconds(59));
        Check(!first.IsCompleted && !second.IsCompleted && fixture.Repository.Read() == before);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await Refused(first); await Refused(second);
        Check(fixture.Repository.Read() == before && fixture.Generator.Count == 1 && fixture.Service.Available);
        using var restored = fixture.Service.Enter();
        Check(fixture.Service.Status.Contains("未更换", StringComparison.Ordinal), "Precommit timeout reports that the identity was not changed");
    }

    /// <summary>提交后准备失败保持 pending；修复后同一次 Refresh 可继续，不再生成，历史重试保持原结果。</summary>
    public static async Task FailureAndResume()
    {
        using var fixture = new MaintenanceFixture(); await fixture.Service.Prepare(default);
        fixture.Preparation.Action = _ => throw new IOException("fixture-secret-path-must-not-escape");
        var operation = Guid.NewGuid().ToString("N");
        var error = await Refused(fixture.Service.Refresh(operation, 1, default));
        var pending = fixture.Repository.Read();
        Check(pending.Pending && pending.Version == 2 && fixture.Generator.Count == 2 && !fixture.Service.Available);
        Check(!error.Message.Contains("fixture-secret", StringComparison.Ordinal) && !fixture.Service.Status.Contains(pending.DeviceId, StringComparison.Ordinal));
        Denied(fixture.Service); fixture.Preparation.Action = null;
        var done = await fixture.Service.Refresh(operation, 1, default);
        Check(!done.Pending && fixture.Generator.Count == 2 && fixture.Service.Current == pending with { Pending = false });
        var second = await fixture.Service.Refresh(Guid.NewGuid().ToString("N"), 2, default);
        var repeated = await fixture.Service.Refresh(operation, 1, default);
        Check(repeated.Version == 2 && !repeated.Pending && repeated.Current == second.Current && fixture.Generator.Count == 3);
        var preparationCalls = fixture.Preparation.Calls;
        using var live = fixture.Service.Enter();
        await Refused(fixture.Service.Resume(Guid.NewGuid().ToString("N"), default));
        Check(fixture.Service.Available && fixture.Preparation.Calls == preparationCalls, "Unknown resume must not rewrite configuration or stop existing availability");
        live.Dispose();
        fixture.Preparation.Action = _ => throw new IOException("fixture later alignment failure");
        var laterOperation = Guid.NewGuid().ToString("N"); await Refused(fixture.Service.Refresh(laterOperation, 3, default));
        var laterPending = fixture.Repository.Read();
        var oldResult = await fixture.Service.Refresh(operation, 1, default);
        Check(oldResult.Version == 2 && !oldResult.Pending && oldResult.Current == laterPending && laterPending.Pending
            && fixture.Repository.Read() == laterPending && !fixture.Service.Available, "Historical retry cannot complete or disturb a newer pending operation");
        fixture.Preparation.Action = null; await fixture.Service.Resume(laterOperation, default);
    }

    /// <summary>30 秒准备超时保持目标；不配合取消的旧准备真退出后，恢复才可启动下一次文件准备。</summary>
    public static async Task AlignmentTimeout()
    {
        using var fixture = new MaintenanceFixture(); await fixture.Service.Prepare(default);
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Preparation.Action = _ => { started.TrySetResult(); release.Wait(); };
        var operation = Guid.NewGuid().ToString("N");
        var refresh = fixture.Service.Refresh(operation, 1, default); await started.Task;
        fixture.Clock.Advance(TimeSpan.FromSeconds(30)); await Refused(refresh);
        var pending = fixture.Repository.Read();
        Check(pending.Pending && !fixture.Service.Available && fixture.Generator.Count == 2);
        var oldCalls = fixture.Preparation.Calls; fixture.Preparation.Action = null;
        var resume = fixture.Service.Resume(operation, default);
        Check(!resume.IsCompleted && fixture.Preparation.Calls == oldCalls, "Resume must wait for the old file writer to exit");
        release.Set(); var done = await resume;
        Check(!done.Pending && fixture.Service.Available && fixture.Generator.Count == 2 && fixture.Preparation.MaximumConcurrent == 1);
        Check(fixture.Service.Current == pending with { Pending = false });
    }

    /// <summary>普通启动准备失败不生成身份；重新准备可恢复，同版本两个独立操作竞争只接受一个。</summary>
    public static async Task PreparationAndVersionRace()
    {
        using var fixture = new MaintenanceFixture(); fixture.Preparation.Action = _ => throw new IOException("fixture preparation failure");
        await fixture.Service.Prepare(default);
        Check(!fixture.Service.Available && fixture.Generator.Count == 1 && !fixture.Repository.Read().Pending);
        fixture.Preparation.Action = null; await fixture.Service.Prepare(default);
        Check(fixture.Service.Available && fixture.Generator.Count == 1);
        using var release = new ManualResetEventSlim(); var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Preparation.Action = cancellation => { started.TrySetResult(); release.Wait(cancellation); };
        var first = fixture.Service.Refresh(Guid.NewGuid().ToString("N"), 1, default); await started.Task;
        var second = fixture.Service.Refresh(Guid.NewGuid().ToString("N"), 1, default);
        release.Set(); await first; await Refused(second);
        Check(fixture.Generator.Count == 2 && fixture.Service.Current.Version == 2 && fixture.Service.Available);
        await ReadyCommitBusy();
    }

    /// <summary>派生文件已经完成但ready提交遇到真实SQLite写锁时仍保持pending，解锁后继续同目标。</summary>
    private static async Task ReadyCommitBusy()
    {
        using var fixture = new MaintenanceFixture(); await fixture.Service.Prepare(default);
        Microsoft.Data.Sqlite.SqliteConnection? blocker = null;
        Microsoft.Data.Sqlite.SqliteTransaction? transaction = null;
        fixture.Preparation.Action = _ => { blocker = fixture.Database.Open(); transaction = blocker.BeginTransaction(); };
        var operation = Guid.NewGuid().ToString("N"); var elapsed = System.Diagnostics.Stopwatch.StartNew();
        SiteIdentitySnapshot pending;
        try
        {
            await Refused(fixture.Service.Refresh(operation, 1, default)); elapsed.Stop();
            pending = fixture.Repository.Read();
            Check(transaction is not null && elapsed.Elapsed < TimeSpan.FromSeconds(3) && pending.Pending && !fixture.Service.Available && fixture.Generator.Count == 2,
                "Ready commit must fail promptly and retain pending when another connection owns the writer lock");
        }
        // 模拟外部管理写入收尾；失败路径也必须释放本测试的SQLite写锁。
        finally { transaction?.Dispose(); blocker?.Dispose(); }
        fixture.Preparation.Action = null;
        var completed = await fixture.Service.Resume(operation, default);
        Check(!completed.Pending && completed.Current == pending with { Pending = false } && fixture.Generator.Count == 2 && fixture.Service.Available);
    }

    /// <summary>只有本站维护拒绝异常才算成功，其他异常保持测试失败。</summary>
    private static void Denied(ISiteIdentityService service)
    {
        var denied = false;
        try { using var unexpected = service.Enter(); }
        // 新的官方操作在维护中必须明确拒绝，不得当作上游认证失败。
        catch (SiteIdentityUnavailableException) { denied = true; }
        Check(denied, "Official admission must be closed");
    }

    /// <summary>等待明确维护拒绝，不把超时挂起或非预期异常视为通过。</summary>
    private static async Task<InvalidOperationException> Refused(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(10)); }
        // 业务错误是预期结果，测试自身十秒保护超时仍直接失败。
        catch (InvalidOperationException error) { return error; }
        throw new Exception("Expected a maintenance refusal");
    }

    /// <summary>每例拥有独立站点，使用真实SQLite与应用协调器，只有文件准备和时钟受控。</summary>
    private sealed class MaintenanceFixture : IDisposable
    {
        private readonly TestHome home = new();
        private readonly ServiceProvider provider;
        /// <summary>可直接推进到截止时间的测试时钟。</summary>
        public UsageClock Clock { get; } = new(DateTimeOffset.Parse("2026-11-12T00:00:00Z"));
        /// <summary>统计所有站点编号生成调用。</summary>
        public MaintenanceGenerator Generator { get; } = new();
        /// <summary>控制准备成功、失败或阻塞的假文件边界。</summary>
        public ControlledPreparation Preparation { get; } = new();
        /// <summary>真实协调器。</summary>
        public ISiteIdentityService Service { get; }
        /// <summary>真实持久身份仓储。</summary>
        public ISiteIdentityRepository Repository { get; }
        /// <summary>制造实际外部写锁竞争的测试数据库。</summary>
        public RelayDatabase Database => provider.GetRequiredService<RelayDatabase>();
        /// <summary>按正式组合根登记，随后只替换受控的外部依赖。</summary>
        public MaintenanceFixture()
        {
            var services = new ServiceCollection().AddHouseholdRelay(options => options.Home = home.Path);
            services.AddSingleton<TimeProvider>(Clock); services.AddSingleton<IDeviceIdGenerator>(Generator);
            services.AddSingleton<IClaudeConfigurationPreparation>(Preparation);
            provider = services.BuildServiceProvider(); Service = provider.GetRequiredService<ISiteIdentityService>();
            Repository = provider.GetRequiredService<ISiteIdentityRepository>();
        }
        /// <summary>先释放容器的数据库资源，再删除测试专属临时目录。</summary>
        public void Dispose() { provider.Dispose(); home.Dispose(); }
    }

    /// <summary>测试专用编号生成器，便于检查重试是否额外换号。</summary>
    private sealed class MaintenanceGenerator : IDeviceIdGenerator
    {
        private int count;
        /// <summary>累计生成次数。</summary>
        public int Count => Volatile.Read(ref count);
        /// <summary>线程安全地产生逐次不同、格式合法的假编号。</summary>
        public string Generate() => Interlocked.Increment(ref count).ToString("x64");
    }

    /// <summary>无需真实CLI的文件准备替身；统计并发来发现超时后重入写文件。</summary>
    private sealed class ControlledPreparation : IClaudeConfigurationPreparation
    {
        private int calls;
        private int active;
        private int maximum;
        /// <summary>下一次准备执行的受控动作，空表示成功。</summary>
        public Action<CancellationToken>? Action { get; set; }
        /// <summary>总准备次数。</summary>
        public int Calls => Volatile.Read(ref calls);
        /// <summary>曾同时进行的准备数量。</summary>
        public int MaximumConcurrent => Volatile.Read(ref maximum);
        /// <summary>未被本组协调器测试读取的假编号。</summary>
        public string DeviceId => new('a', 64);
        /// <summary>本组测试不准备单个账户。</summary>
        public void PrepareAccount(string accountId, bool allowCreate = false) => throw new NotSupportedException();
        /// <summary>本组测试不读取账户目录。</summary>
        public void ValidateAccount(string accountId, bool reread = false) => throw new NotSupportedException();
        /// <summary>本组测试不启动诊断CLI。</summary>
        public void PrepareTransient(string configDirectory) => throw new NotSupportedException();
        /// <summary>执行同步准备并完整登记生命周期，取消与异常都归还活跃计数。</summary>
        public void PrepareExistingAccounts(CancellationToken cancellation = default)
        {
            Interlocked.Increment(ref calls); var concurrent = Interlocked.Increment(ref active);
            Interlocked.Exchange(ref maximum, Math.Max(MaximumConcurrent, concurrent));
            try { Action?.Invoke(cancellation); }
            // 即使故障或取消也表示文件边界已经退出，后续恢复才可安全重试。
            finally { Interlocked.Decrement(ref active); }
        }
    }
}
