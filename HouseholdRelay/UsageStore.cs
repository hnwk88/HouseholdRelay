using Microsoft.Data.Sqlite;
using static HouseholdRelay.RelayDatabase;

namespace HouseholdRelay;

/// <summary>每次操作独立提交，结束保存与请求取消无关；失败会进入可见健康状态和日志。</summary>
public sealed class UsageStore(RelayDatabase database, TimeProvider? timeProvider = null, Action<object>? log = null)
{
    public TimeProvider Clock { get; } = timeProvider ?? TimeProvider.System;
    public long PersistenceFailures => Interlocked.Read(ref failures);
    private long failures;
    private readonly SemaphoreSlim writes = new(1, 1);
    public DateTimeOffset Now => Clock.GetUtcNow();
    public DateTimeOffset Cutoff => Now.AddDays(-40);
    internal RelayDatabase Database => database;

    public async Task Start(string requestId, ClientAccess client, string kind)
    {
        var acquired = false; var startedAt = Now;
        try
        {
            acquired = await writes.WaitAsync(TimeSpan.FromSeconds(2));
            if (!acquired) throw new InvalidOperationException("Usage write queue is busy.");
            using var connection = database.Open(1);
            Execute(connection, null, """
                INSERT INTO usage_requests(request_id,user_id,token_id,upstream_version,request_started_at,kind)
                VALUES($id,$user,$token,(SELECT version FROM upstream WHERE id=1),$time,$kind)
                """, ("$id", requestId), ("$user", client.Id), ("$token", client.TokenId), ("$time", startedAt.UtcTicks), ("$kind", kind));
        }
        catch (Exception error) when (StorageError(error))
        { Failure(requestId, "start"); throw new RelayFailure(503, "Usage storage unavailable; request was not sent."); }
        finally { if (acquired) writes.Release(); }
    }

    public async Task Dispatch(string requestId, long version, string? model)
    {
        var acquired = false;
        try
        {
            acquired = await writes.WaitAsync(TimeSpan.FromSeconds(2));
            if (!acquired) throw new InvalidOperationException("Usage write queue is busy.");
            using var connection = database.Open(1);
            if (Execute(connection, null, """
                UPDATE usage_requests SET upstream_version=$version,model=$model,dispatched=1,
                    price_json=(SELECT json FROM usage_prices WHERE upstream_version=$version AND model=$model)
                WHERE request_id=$id AND outcome='started'
                """, ("$id", requestId), ("$version", version), ("$model", model)) != 1)
                throw new InvalidOperationException("Usage start record unavailable.");
        }
        catch (Exception error) when (StorageError(error))
        { Failure(requestId, "dispatch"); throw new RelayFailure(503, "Usage storage unavailable; request was not sent."); }
        finally { if (acquired) writes.Release(); }
    }

    public async Task<bool> Finish(string requestId, UsageEnd result)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var acquired = false;
            try
            {
                acquired = await writes.WaitAsync(TimeSpan.FromSeconds(2));
                if (!acquired) throw new InvalidOperationException("Usage write queue is busy.");
                using var connection = database.Open(1);
                using var transaction = connection.BeginTransaction();
                string kind; string? priceJson;
                using (var command = Command(connection, transaction, "SELECT outcome,kind,price_json FROM usage_requests WHERE request_id=$id", ("$id", requestId)))
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read()) throw new InvalidOperationException("Usage start record unavailable.");
                    if (reader.GetString(0) != "started") return true;
                    kind = reader.GetString(1); priceJson = reader.IsDBNull(2) ? null : reader.GetString(2);
                }
                var money = UsagePricing.Calculate(kind, result, priceJson);
                // 条件更新使重复结束通知不覆盖首次已提交结果。
                Execute(connection, transaction, """
                    UPDATE usage_requests SET ended_at=$time,outcome=$outcome,dispatched=$sent,status_code=$status,
                        upstream_status=$upstream,duration_ms=$duration,completeness=$completeness,
                        input_tokens=$input,output_tokens=$output,cache_read_tokens=$read,cache_write_tokens=$write,
                        amount=$amount,currency=$currency,money_source=$source,money_completeness=$moneyState
                    WHERE request_id=$id AND outcome='started'
                    """, ("$id", requestId), ("$time", Now.UtcTicks), ("$outcome", result.Outcome), ("$sent", result.Dispatched),
                    ("$status", result.Status), ("$upstream", result.UpstreamStatus), ("$duration", result.DurationMs),
                    ("$completeness", result.Completeness), ("$input", result.Tokens.Input), ("$output", result.Tokens.Output),
                    ("$read", result.Tokens.CacheRead), ("$write", result.Tokens.CacheWrite),
                    ("$amount", UsagePricing.DecimalText(money.Amount)), ("$currency", money.Currency), ("$source", money.Source), ("$moneyState", money.Completeness));
                transaction.Commit();
                return true;
            }
            catch (Exception error) when (StorageError(error))
            {
                if (attempt == 2) { Failure(requestId, "finish"); return false; }
                if (acquired) { writes.Release(); acquired = false; }
                await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), CancellationToken.None);
            }
            finally { if (acquired) writes.Release(); }
        }
        return false;
    }

    public void Recover()
    {
        using var connection = database.Open(1);
        Execute(connection, null, """
            UPDATE usage_requests SET outcome='interrupted',completeness='partial',money_completeness='partial',ended_at=$time
            WHERE outcome='started'
            """, ("$time", Now.UtcTicks));
    }

    public async Task<int> Cleanup(CancellationToken cancellation = default)
    {
        var cutoff = Cutoff.UtcTicks; var removed = 0; int batch;
        do
        {
            cancellation.ThrowIfCancellationRequested();
            using (var connection = database.Open(1))
                batch = Execute(connection, null, """
                    DELETE FROM usage_requests WHERE request_id IN
                    (SELECT request_id FROM usage_requests WHERE request_started_at < $cutoff ORDER BY request_started_at LIMIT 500)
                    """, ("$cutoff", cutoff));
            removed += batch;
            if (batch == 500) await Task.Yield();
        } while (batch == 500);
        return removed;
    }

    public UsageReport Query(UsageFilter filter) => UsageQuery.Read(this, filter);
    internal void MaintenanceFailure() => Failure(null, "maintenance");
    private void Failure(string? id, string stage)
    {
        Interlocked.Increment(ref failures);
        log?.Invoke(new { time = Now, eventType = "usage_storage_failure", requestId = id, stage });
    }
    internal static bool StorageError(Exception error) => error is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException;
}

/// <summary>只在服务启动时恢复遗留请求；查询和 CLI 打开数据库不会打断仍在运行的请求。</summary>
internal sealed class UsageMaintenance(UsageStore store) : BackgroundService
{
    private FileStream? serving;
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            serving = new FileStream(Path.Combine(store.Database.State, "serving.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            store.Recover(); await store.Cleanup(cancellationToken);
            await base.StartAsync(cancellationToken);
        }
        catch { serving?.Dispose(); serving = null; throw; }
    }
    public override async Task StopAsync(CancellationToken cancellationToken)
    { try { await base.StopAsync(cancellationToken); } finally { serving?.Dispose(); serving = null; } }
    public override void Dispose() { base.Dispose(); serving?.Dispose(); serving = null; }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), store.Clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { await store.Cleanup(stoppingToken); }
                catch (Exception error) when (UsageStore.StorageError(error)) { store.MaintenanceFailure(); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
