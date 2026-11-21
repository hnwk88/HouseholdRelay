using System.Diagnostics;
using System.Globalization;
using System.Net;
using Application.Interfaces.Upstream;
using Application.Interfaces.Usage;
using Application.Services.Identity;
using Application.Services.Usage;
using Core.Identity;
using Core.Upstream;
using Core.Usage;
using Microsoft.Extensions.DependencyInjection;
using static Tests.TestSuite;
using static Tests.UsageFixture;

namespace Tests;

/// <summary>
/// 转发开销基准（命令行 <c>--db-time-benchmark 次数 输出文件</c>）：本机模拟上游，单个使用者顺序转发，每个请求把花在库上的时间加起来——
/// 鉴权前重读已提交名单（每次 Authenticate 一次）、连接池读活动上游、用量开始、发送、结束三次写。三处都在夹具的登记入口里换成计时装饰器，不改产品代码。
/// 预热 20 次不计；每人每分钟限流调到上限 10000，免得顺序请求被限流。订单 151 用它对比改动前（ee78bd4，同一计时点的同步版本）与改动后。
/// </summary>
static class DbTimeBenchmark
{
    private static long ticks;
    private static long finishes;

    /// <summary>执行一次库操作并把耗时累加进总数。</summary>
    private static async Task<T> Timed<T>(Func<Task<T>> operation)
    {
        var started = Stopwatch.GetTimestamp();
        try { return await operation(); }
        // 成功或抛出都算这次库上耗时，失败的操作也占用了库。
        finally { Interlocked.Add(ref ticks, Stopwatch.GetTimestamp() - started); }
    }

    /// <summary>执行一次没有返回值的库操作并把耗时累加进总数。</summary>
    private static Task Timed(Func<Task> operation) => Timed(async () => { await operation(); return 0; });

    /// <summary>跑 <c>args[1]</c> 次（默认 200）转发，把每次的库上耗时与中位数、第 95 百分位、平均写进 <c>args[2]</c>，并打到控制台。</summary>
    public static async Task<int> Run(string[] args)
    {
        var count = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 200;
        var output = args.Length > 2 ? args[2] : "db-time.txt";
        await using var upstream = await UpstreamFixture.Create();
        await using var web = await WebFixture.Create(configure: config => config.RequestsPerMinute = 10000, overrides: services =>
        {
            services.AddSingleton<IActiveUpstreamSource>(provider => new TimedSource(provider.GetRequiredService<IUpstreamService>()));
            services.AddSingleton<IUsageService>(provider => new TimedUsage(ActivatorUtilities.CreateInstance<UsageService>(provider)));
            services.AddSingleton(provider =>
            {
                var scopes = provider.GetRequiredService<IServiceScopeFactory>();
                return new ClientRegistry(IdentityService.ReadCommitted(scopes).GetAwaiter().GetResult(), () => Timed(() => IdentityService.ReadCommitted(scopes)));
            });
        });
        var user = await web.Identities.Create("基准");
        await upstream.Activate(web.Upstreams);
        upstream.Handler = context => Json(context, new { input_tokens = 10, output_tokens = 11 });
        var samples = new List<double>();
        for (var index = 0; index < count + 20; index++)
        {
            var before = Interlocked.Read(ref ticks); var finished = Interlocked.Read(ref finishes);
            var status = await Call(web, user.Token); Check(status == HttpStatusCode.OK, $"Benchmark request {index} failed: {status}");
            await Until(() => Interlocked.Read(ref finishes) > finished);
            // 前 20 次是预热（连接、JIT、EF 模型），不计入。
            if (index >= 20) samples.Add((Interlocked.Read(ref ticks) - before) * 1000.0 / Stopwatch.Frequency);
        }
        var sorted = samples.Order().ToArray();
        double Percentile(double p) => sorted[(int)Math.Ceiling(p * sorted.Length) - 1];
        var lines = new List<string>
        {
            $"count={sorted.Length}",
            $"median_ms={Percentile(0.5).ToString("0.000", CultureInfo.InvariantCulture)}",
            $"p95_ms={Percentile(0.95).ToString("0.000", CultureInfo.InvariantCulture)}",
            $"mean_ms={sorted.Average().ToString("0.000", CultureInfo.InvariantCulture)}",
        };
        lines.AddRange(samples.Select(sample => sample.ToString("0.000", CultureInfo.InvariantCulture)));
        await File.WriteAllLinesAsync(output, lines);
        Console.WriteLine(string.Join(" ", lines.Take(4)));
        return 0;
    }

    /// <summary>给连接池读活动上游计时。</summary>
    private sealed class TimedSource(IActiveUpstreamSource inner) : IActiveUpstreamSource
    {
        /// <summary>计时读活动上游。</summary>
        public Task<UpstreamSnapshot> ReadActive() => Timed(inner.ReadActive);
        /// <summary>计时读备用上游。</summary>
        public Task<IReadOnlyList<UpstreamSnapshot>> ReadStandby() => Timed(inner.ReadStandby);
    }

    /// <summary>给用量的开始、发送、结束三次写计时，结束完成时记一次。</summary>
    private sealed class TimedUsage(IUsageService inner) : IUsageService
    {
        /// <summary>原样转交。</summary>
        public TimeProvider Clock => inner.Clock;
        /// <summary>原样转交。</summary>
        public long PersistenceFailures => inner.PersistenceFailures;
        /// <summary>原样转交。</summary>
        public DateTimeOffset Now => inner.Now;
        /// <summary>原样转交。</summary>
        public DateTimeOffset Cutoff => inner.Cutoff;
        /// <summary>计时写开始记录。</summary>
        public Task Start(string requestId, ClientAccess client, UsageKind kind) => Timed(() => inner.Start(requestId, client, kind));
        /// <summary>计时写发送标记。</summary>
        public Task Dispatch(string requestId, long version, long? upstreamCandidateId, string? model) => Timed(() => inner.Dispatch(requestId, version, upstreamCandidateId, model));
        /// <summary>计时写结束结果，完成时记一次。</summary>
        public async Task<bool> Finish(string requestId, UsageEnd result)
        {
            try { return await Timed(() => inner.Finish(requestId, result)); }
            // 无论写成没写成都记一次完成，基准循环靠它判断这一请求的库操作已结束。
            finally { Interlocked.Increment(ref finishes); }
        }
        /// <summary>原样转交。</summary>
        public Task Recover() => inner.Recover();
        /// <summary>原样转交。</summary>
        public Task<int> Cleanup(CancellationToken cancellation = default) => inner.Cleanup(cancellation);
        /// <summary>原样转交。</summary>
        public Task<UsageReport> Query(UsageFilter filter) => inner.Query(filter);
        /// <summary>原样转交。</summary>
        public void MaintenanceFailure() => inner.MaintenanceFailure();
    }
}
