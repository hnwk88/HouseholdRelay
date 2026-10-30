using System.Data.Common;
using Application.Interfaces.Relay;
using Application.Interfaces.Usage;
using Core.Identity;
using Core.Relay;
using Core.Usage;

namespace Application.Services.Usage;

/// <summary>每次操作独立提交，结束保存与请求取消无关；失败会进入可见健康状态和日志。</summary>
public sealed class UsageService(IUsageRepository repository, TimeProvider timeProvider, IRelayEventLog log) : IUsageService
{
    /// <summary>用量时间戳与维护计时器共用的时钟；由注入的 <see cref="TimeProvider"/> 提供，测试可替换。</summary>
    public TimeProvider Clock { get; } = timeProvider;

    /// <summary>本进程启动以来累计的用量保存失败次数（含后台维护失败），线程安全读取；重启后归零。</summary>
    public long PersistenceFailures => Interlocked.Read(ref failures);
    private long failures;
    private readonly SemaphoreSlim writes = new(1, 1);

    /// <summary>当前 UTC 时间，取自 <see cref="Clock"/>。</summary>
    public DateTimeOffset Now => Clock.GetUtcNow();

    /// <summary>40 天保留窗口的截止时间：早于此刻的记录会被清理。</summary>
    public DateTimeOffset Cutoff => Now.AddDays(-40);

    /// <summary>
    /// 在发往上游之前写入请求开始记录（归属取自已鉴权的使用者与令牌）。写入串行进行，最多等待 2 秒；
    /// 等不到或存储故障时计入失败并抛出 503 的 <see cref="RelayFailure"/>，请求不会发往上游。
    /// </summary>
    public async Task Start(string requestId, ClientAccess client, string kind)
    {
        var acquired = false; var startedAt = Now;
        try
        {
            acquired = await writes.WaitAsync(TimeSpan.FromSeconds(2));
            if (!acquired) throw new InvalidOperationException("Usage write queue is busy.");
            repository.InsertStart(requestId, client.Id, client.TokenId, startedAt.UtcTicks, kind);
        }
        // 记不下开始记录就不能发出请求，否则会产生无法计量的上游调用；只把存储类故障转成 503，不暴露底层细节，其他异常照常上抛。
        catch (Exception error) when (StorageError(error))
        { Failure(requestId, "start"); throw new RelayFailure(503, "Usage storage unavailable; request was not sent."); }
        // 只在确实拿到写入名额时归还，避免等待超时后错误地多释放一次信号量。
        finally { if (acquired) writes.Release(); }
    }

    /// <summary>
    /// 即将发往上游时把开始记录标记为已发送、记下首先要试的那一家，并让仓储复制它当时的单价快照。开始记录不存在或已不在开始状态、写入排队超时或存储故障时，
    /// 计入失败并抛出 503 的 <see cref="RelayFailure"/>，请求不会发往上游。
    /// </summary>
    public async Task Dispatch(string requestId, long version, string? candidateId, string? model)
    {
        var acquired = false;
        try
        {
            acquired = await writes.WaitAsync(TimeSpan.FromSeconds(2));
            if (!acquired) throw new InvalidOperationException("Usage write queue is busy.");
            if (repository.MarkDispatched(requestId, version, candidateId, model) != 1)
                throw new InvalidOperationException("Usage start record unavailable.");
        }
        // 发送标记或单价快照没写成功就不发出请求，保证每次上游调用都有可计费的记录；只转换存储类故障，其他异常照常上抛。
        catch (Exception error) when (StorageError(error))
        { Failure(requestId, "dispatch"); throw new RelayFailure(503, "Usage storage unavailable; request was not sent."); }
        // 只在确实拿到写入名额时归还，避免等待超时后错误地多释放一次信号量。
        finally { if (acquired) writes.Release(); }
    }

    /// <summary>
    /// 保存请求结束结果并按发送时的单价快照计算金额，成功返回 true。存储故障时最多共尝试 3 次（间隔 100、200 毫秒），
    /// 全部失败则计入失败并返回 false，不抛出。重试等待不受请求取消影响，客户端断开也会尽量把结果写完。
    /// </summary>
    public async Task<bool> Finish(string requestId, UsageEnd result)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var acquired = false;
            try
            {
                acquired = await writes.WaitAsync(TimeSpan.FromSeconds(2));
                if (!acquired) throw new InvalidOperationException("Usage write queue is busy.");
                // 金额只由纯规则按发送时的单价快照计算；仓储只负责在同一事务里读快照、写结果。
                repository.Complete(requestId, Now.UtcTicks, result, (kind, priceJson) => UsageMoneyRules.Calculate(kind, result, priceJson));
                return true;
            }
            // 结束记录写失败不应让已完成的请求报错，所以只记失败并有限重试；重试前先归还写入名额，避免退避等待期间挡住其他请求的写入。
            // 退避等待故意不接收请求的取消令牌：结束保存与请求取消无关，客户端断开后也要尽量写完。
            catch (Exception error) when (StorageError(error))
            {
                if (attempt == 2) { Failure(requestId, "finish"); return false; }
                if (acquired) { writes.Release(); acquired = false; }
                await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), CancellationToken.None);
            }
            // 只在本轮仍持有写入名额时归还；catch 里已提前归还的会把标记清掉，防止重复释放。
            finally { if (acquired) writes.Release(); }
        }
        return false;
    }

    /// <summary>服务启动时把上次遗留、仍处于开始状态的记录统一标记为中断（以当前时间为结束时间）。</summary>
    public void Recover() => repository.Recover(Now.UtcTicks);

    /// <summary>
    /// 以调用时刻计算一次截止点，分批（每批最多 500 行）删除超出 40 天保留窗口的记录，返回删除总数。
    /// 每批之前检查取消；满批时让出线程再继续，避免长时间占用。
    /// </summary>
    public async Task<int> Cleanup(CancellationToken cancellation = default)
    {
        var cutoff = Cutoff.UtcTicks; var removed = 0; int batch;
        do
        {
            cancellation.ThrowIfCancellationRequested();
            batch = repository.DeleteExpiredBatch(cutoff);
            removed += batch;
            if (batch == 500) await Task.Yield();
        } while (batch == 500);
        return removed;
    }

    /// <summary>校验筛选条件后在 40 天窗口内查询用量报告，并附上本进程的保存失败次数；条件不合法时抛出 <see cref="InvalidOperationException"/>。</summary>
    public UsageReport Query(UsageFilter filter)
    {
        filter.Validate();
        return repository.Query(filter, Now, PersistenceFailures);
    }

    /// <summary>记录一次后台维护（定时清理）失败：计入失败次数并写事件日志。</summary>
    public void MaintenanceFailure() => Failure(null, "maintenance");

    /// <summary>原子地累加失败次数，并写一条只含时间、请求编号和阶段的事件日志，不记录异常细节。</summary>
    private void Failure(string? id, string stage)
    {
        Interlocked.Increment(ref failures);
        log.Write(new { time = Now, eventType = "usage_storage_failure", requestId = id, stage });
    }

    /// <summary>可恢复的存储故障：数据库异常、文件访问失败或写入队列与记录状态不满足；这些故障只影响用量，不暴露细节。</summary>
    public static bool StorageError(Exception error) => error is DbException or IOException or UnauthorizedAccessException or InvalidOperationException;
}
