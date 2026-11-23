using System.Data.Common;
using Application.DependencyInjection;
using Application.Interfaces.Relay;
using Application.Interfaces.Usage;
using Core.Identity;
using Core.Relay;
using Core.Upstream;
using Core.Usage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Repositories;

namespace Application.Services.Usage;

/// <summary>
/// 每次操作独立提交，结束保存与请求取消无关；失败会进入可见健康状态和日志。用量记录（usage_requests）与上游尝试（usage_attempts）经通用仓储存取，
/// 每次写入一个作用域、一个短事务，不跨越网络等待；状态推进由 <see cref="UsageRequest"/> 的方法判断。
/// </summary>
public sealed class UsageService(IServiceScopeFactory scopes, TimeProvider timeProvider, IRelayEventLog log) : IUsageService
{
    /// <summary>每批清理的最多行数。</summary>
    private const int CleanupBatch = 500;

    /// <summary>用量写入等写锁的上限（与改造前的 1 秒连接相同）：库被别人占着时很快失败，转发不被拖住。</summary>
    private static readonly TimeSpan _writeWait = TimeSpan.FromSeconds(1);

    /// <summary>用量查询等锁的上限（与改造前的 2 秒连接相同）。</summary>
    private static readonly TimeSpan _queryWait = TimeSpan.FromSeconds(2);

    /// <summary>用量时间戳与维护计时器共用的时钟；由注入的 <see cref="TimeProvider"/> 提供，测试可替换。</summary>
    public TimeProvider Clock { get; } = timeProvider;

    /// <summary>本进程启动以来累计的用量保存失败次数（含后台维护失败），线程安全读取；重启后归零。</summary>
    public long PersistenceFailures => Interlocked.Read(ref failures);
    private long failures;
    private readonly SemaphoreSlim _writes = new(1, 1);

    /// <summary>当前 UTC 时间，取自 <see cref="Clock"/>。</summary>
    public DateTimeOffset Now => Clock.GetUtcNow();

    /// <summary>40 天保留窗口的截止时间：早于此刻的记录会被清理。</summary>
    public DateTimeOffset Cutoff => UsageRetention.Cutoff(Now);

    /// <summary>
    /// 在发往上游之前写入请求开始记录（归属取自已鉴权的使用者与令牌，版本记此刻的活动版本）。写入串行进行，最多等待 2 秒；
    /// 等不到或存储故障时计入失败并抛出 503 的 <see cref="RelayFailure"/>，请求不会发往上游。
    /// </summary>
    public async Task Start(string requestId, ClientAccess client, UsageKind kind)
    {
        var acquired = false; var startedAt = Now;
        try
        {
            acquired = await _writes.WaitAsync(TimeSpan.FromSeconds(2));
            if (!acquired) throw new InvalidOperationException("Usage write queue is busy.");
            await using var scope = scopes.CreateAsyncScope();
            var requests = scope.Repository<UsageRequest>();
            await requests.WithLockWaitAsync(_writeWait, async () =>
            {
                var version = (await scope.Repository<ActiveUpstream>().GetByIdAsync(ActiveUpstream.SingletonId))?.Version;
                await requests.AddAsync(UsageRequest.Start(requestId, client.UserId, client.TokenId, version, startedAt.UtcTicks, kind));
                return await requests.SaveChangesAsync();
            });
        }
        // 记不下开始记录就不能发出请求，否则会产生无法计量的上游调用；只把存储类故障转成 503，不暴露底层细节，其他异常照常上抛。
        catch (Exception error) when (StorageError(error))
        { Failure(requestId, "start"); throw new RelayFailure(503, "Usage storage unavailable; request was not sent."); }
        // 只在确实拿到写入名额时归还，避免等待超时后错误地多释放一次信号量。
        finally { if (acquired) _writes.Release(); }
    }

    /// <summary>
    /// 即将发往上游时把开始记录标记为已发送、记下首先要试的那一家，并复制它当时的单价快照与名称快照。开始记录不存在或已不在开始状态、写入排队超时或存储故障时，
    /// 计入失败并抛出 503 的 <see cref="RelayFailure"/>，请求不会发往上游。
    /// </summary>
    public async Task Dispatch(string requestId, long version, long? upstreamCandidateId, string? model)
    {
        var acquired = false;
        try
        {
            acquired = await _writes.WaitAsync(TimeSpan.FromSeconds(2));
            if (!acquired) throw new InvalidOperationException("Usage write queue is busy.");
            await using var scope = scopes.CreateAsyncScope();
            var requests = scope.Repository<UsageRequest>();
            await requests.WithLockWaitAsync(_writeWait, async () =>
            {
                var row = await requests.FirstOrDefaultAsync(item => item.RequestId == requestId);
                var (price, name) = await Snapshot(scope, upstreamCandidateId, model);
                if (row is null || !row.Dispatch(version, upstreamCandidateId, model, price, name))
                    throw new InvalidOperationException("Usage start record unavailable.");
                return await requests.SaveChangesAsync();
            });
        }
        // 发送标记或单价快照没写成功就不发出请求，保证每次上游调用都有可计费的记录；只转换存储类故障，其他异常照常上抛。
        catch (Exception error) when (StorageError(error))
        { Failure(requestId, "dispatch"); throw new RelayFailure(503, "Usage storage unavailable; request was not sent."); }
        // 只在确实拿到写入名额时归还，避免等待超时后错误地多释放一次信号量。
        finally { if (acquired) _writes.Release(); }
    }

    /// <summary>
    /// 保存请求结束结果并按单价快照计算金额：在一个事务里读出记录，由 <see cref="UsageRequest.ServedBy"/> 定下记在哪一家（换了家就取那一家此刻的单价与名称），
    /// 写入结束结果与各次尝试，返回 <see cref="UsageFinish.Saved"/>；记录已结束时什么都不写，返回 <see cref="UsageFinish.AlreadyEnded"/>。
    /// 存储故障时最多共尝试 3 次（间隔 100、200 毫秒），全部失败则计入失败并返回 <see cref="UsageFinish.Failed"/>，不抛出。
    /// 重试等待不受请求取消影响，客户端断开也会尽量把结果写完。
    /// </summary>
    public async Task<UsageFinish> Finish(string requestId, UsageEnd result)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var acquired = false;
            try
            {
                acquired = await _writes.WaitAsync(TimeSpan.FromSeconds(2));
                if (!acquired) throw new InvalidOperationException("Usage write queue is busy.");
                return await Complete(requestId, result) ? UsageFinish.Saved : UsageFinish.AlreadyEnded;
            }
            // 结束记录写失败不应让已完成的请求报错，所以只记失败并有限重试；重试前先归还写入名额，避免退避等待期间挡住其他请求的写入。
            // 退避等待故意不接收请求的取消令牌：结束保存与请求取消无关，客户端断开后也要尽量写完。
            catch (Exception error) when (StorageError(error))
            {
                if (attempt == 2) { Failure(requestId, "finish"); return UsageFinish.Failed; }
                if (acquired) { _writes.Release(); acquired = false; }
                await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), CancellationToken.None);
            }
            // 只在本轮仍持有写入名额时归还；catch 里已提前归还的会把标记清掉，防止重复释放。
            finally { if (acquired) _writes.Release(); }
        }
        return UsageFinish.Failed;
    }

    /// <summary>服务启动时把上次遗留、仍处于开始状态的记录统一标记为中断（以当前时间为结束时间）。</summary>
    public async Task Recover()
    {
        await using var scope = scopes.CreateAsyncScope();
        var requests = scope.Repository<UsageRequest>();
        var now = Now.UtcTicks;
        await requests.WithLockWaitAsync(_writeWait, async () =>
        {
            foreach (var row in await requests.FindAsync(item => item.Outcome == UsageOutcome.Started)) row.Interrupt(now);
            return await requests.SaveChangesAsync();
        });
    }

    /// <summary>
    /// 以调用时刻计算一次截止点，分批（每批最多 500 行，最早的先删）物理删除超出 40 天保留窗口的记录，它们的上游尝试由外键级联一并删除；返回删除的用量记录总数。
    /// 每批之前检查取消；满批时让出线程再继续，避免长时间占用。
    /// </summary>
    public async Task<int> Cleanup(CancellationToken cancellation)
    {
        var cutoff = Cutoff.UtcTicks; var removed = 0; int batch;
        do
        {
            cancellation.ThrowIfCancellationRequested();
            batch = await DeleteExpiredBatch(cutoff);
            removed += batch;
            if (batch == CleanupBatch) await Task.Yield();
        } while (batch == CleanupBatch);
        return removed;
    }

    /// <summary>
    /// 校验筛选条件后在 40 天窗口内查询用量报告，并附上本进程的保存失败次数：窗口内符合条件的记录一次读出，汇总、分段、金额与当页明细都由 <see cref="UsageReportBuilder"/> 从这同一份算出，互相对得上。
    /// 条件不合法时抛出 <see cref="InvalidOperationException"/>。
    /// </summary>
    public async Task<UsageReport> Query(UsageFilter filter)
    {
        filter.Validate();
        var now = Now; var cutoff = UsageRetention.Cutoff(now);
        var from = (filter.From is { } since && since > cutoff ? since : cutoff).UtcTicks; var until = filter.Until?.UtcTicks; var end = now.UtcTicks;
        var kind = filter.Kind;
        var model = string.IsNullOrEmpty(filter.Model) ? null : filter.Model;
        var outcome = filter.Outcome; var upstream = filter.UpstreamCandidateId;
        // 只读查询不开写事务：SQLite 下写事务是立即写锁，会挡住转发路径上的用量写入；汇总、分段、金额与明细出自下面同一次读取，天然对得上。
        await using var scope = scopes.CreateAsyncScope();
        var requests = scope.Repository<UsageRequest>();
        return await requests.WithLockWaitAsync(_queryWait, async () =>
        {
            var rows = await requests.FindAsync(item => item.RequestStartedAt >= from && item.RequestStartedAt <= end
                && (until == null || item.RequestStartedAt < until) && (filter.UserId == null || item.UserId == filter.UserId)
                && (kind == null || item.Kind == kind) && (model == null || item.Model == model) && (outcome == null || item.Outcome == outcome)
                && (upstream == null || item.UpstreamCandidateId == upstream));
            var names = (await scope.Repository<User>().GetAllAsync()).ToDictionary(item => item.Id, item => item.DisplayName);
            var page = UsageReportBuilder.Page(rows, names, filter);
            var pageIds = page.Select(item => item.Id).ToArray();
            var attempts = await scope.Repository<UsageAttempt>().FindAsync(item => pageIds.Contains(item.UsageRequestId));
            var used = attempts.Select(item => item.UpstreamCandidateId).OfType<long>().Distinct().ToArray();
            var candidates = (await scope.Repository<UpstreamCandidate>().FindAsync(item => used.Contains(item.Id), includeDeleted: true)).ToDictionary(item => item.Id);
            return UsageReportBuilder.Build(rows, page, names, attempts, candidates, filter, cutoff, now, PersistenceFailures);
        });
    }

    /// <summary>记录一次后台维护（定时清理）失败：计入失败次数并写事件日志。</summary>
    public void MaintenanceFailure() => Failure(null, "maintenance");

    /// <summary>
    /// 在一个事务里读出记录、定下记账的那一家与单价快照、算金额，写入结束结果与各次尝试；要么都在，要么都不在，重试保存时不会留下重复的尝试。
    /// 返回这次有没有写：记录早已结束（<see cref="UsageRequest.Complete"/> 拒绝）时为假。
    /// </summary>
    private async Task<bool> Complete(string requestId, UsageEnd result)
    {
        await using var scope = scopes.CreateAsyncScope();
        var requests = scope.Repository<UsageRequest>();
        var written = false;
        await requests.WithLockWaitAsync(_writeWait, () => requests.ExecuteInTransactionAsync(async () =>
        {
            written = false;
            var row = await requests.FirstOrDefaultAsync(item => item.RequestId == requestId) ?? throw new InvalidOperationException("Usage start record unavailable.");
            if (!row.IsStarted) return 0;
            var served = row.ServedBy(result);
            var (price, name) = served == row.UpstreamCandidateId ? (row.PriceJson, row.UpstreamName) : await Snapshot(scope, served, row.Model);
            // 金额只由纯规则按单价快照计算；这里只负责在同一事务里读快照、写结果。实体拒绝（早已结束）就什么都不写。
            if (!row.Complete(Now.UtcTicks, result, UsageMoneyRules.Calculate(row.Kind, result, price), served, price, name)) return 0;
            written = true;
            var attempts = scope.Repository<UsageAttempt>();
            var names = await Names(scope, result.Attempts.Select(item => item.UpstreamCandidateId));
            await attempts.AddRangeAsync(result.Attempts.Select(item => UsageAttempt.From(row.Id,
                item, item.UpstreamCandidateId is { } id ? names.GetValueOrDefault(id) : null)).ToList());
            return await requests.SaveChangesAsync();
        }));
        return written;
    }

    /// <summary>删除一批（最多 <see cref="CleanupBatch"/> 行、最早的先删）早于截止点的记录，返回删除的用量记录行数。</summary>
    private async Task<int> DeleteExpiredBatch(long cutoffTicks)
    {
        await using var scope = scopes.CreateAsyncScope();
        var requests = scope.Repository<UsageRequest>();
        return await requests.WithLockWaitAsync(_writeWait, async () =>
        {
            var (expired, _) = await requests.GetPagedAsync(1, CleanupBatch, item => item.RequestStartedAt < cutoffTicks, item => item.RequestStartedAt);
            // 40 天清理是物理删除：登记后才会真从库里删掉，不会被默认的软删除改回去；尝试记录由外键 ON DELETE CASCADE 一并删除。
            await requests.DeleteRangeAsync(expired, isHardDelete: true);
            await requests.SaveChangesAsync();
            return expired.Count;
        });
    }

    /// <summary>某一家某个模型此刻的单价 JSON 与那一家此刻的名称（停用的也算）；没有那一家时两者都为空。</summary>
    private static async Task<(string? Price, string? Name)> Snapshot(AsyncServiceScope scope, long? upstreamCandidateId, string? model)
    {
        if (upstreamCandidateId is not { } id) return (null, null);
        var price = model is null ? null : await scope.Repository<UsagePriceRecord>().FirstOrDefaultAsync(item => item.UpstreamCandidateId == id && item.Model == model);
        var candidate = await scope.Repository<UpstreamCandidate>().GetByIdAsync(id, includeDeleted: true);
        return (price?.Json, candidate?.Name);
    }

    /// <summary>这些已保存上游此刻的名称（停用的也算）。</summary>
    private static async Task<Dictionary<long, string>> Names(AsyncServiceScope scope, IEnumerable<long?> ids)
    {
        var wanted = ids.OfType<long>().Distinct().ToArray();
        if (wanted.Length == 0) return [];
        return (await scope.Repository<UpstreamCandidate>().FindAsync(item => wanted.Contains(item.Id), includeDeleted: true)).ToDictionary(item => item.Id, item => item.Name);
    }

    /// <summary>原子地累加失败次数，并写一条只含时间、请求编号和阶段的事件日志，不记录异常细节。</summary>
    private void Failure(string? id, string stage)
    {
        Interlocked.Increment(ref failures);
        log.Write(new { time = Now, eventType = "usage_storage_failure", requestId = id, stage });
    }

    /// <summary>可恢复的存储故障：数据库异常（含 EF 写库失败与并发冲突）、文件访问失败或写入队列与记录状态不满足；这些故障只影响用量，不暴露细节。</summary>
    public static bool StorageError(Exception error) => error is DbException or DbUpdateException or IOException or UnauthorizedAccessException or InvalidOperationException;
}
