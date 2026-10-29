using Application.Interfaces.Usage;
using Microsoft.Extensions.Hosting;

namespace Application.Services.Usage;

/// <summary>只在服务启动时恢复遗留请求；查询和 CLI 打开数据库不会打断仍在运行的请求。</summary>
public sealed class UsageMaintenance(IUsageService store, IServingLock servingLock) : BackgroundService
{
    private IDisposable? serving;

    /// <summary>
    /// 服务启动时先取得数据目录的独占服务锁，再把上次遗留的未结束记录标记为中断、清理超出 40 天的记录，最后启动定时任务。
    /// 已有另一服务进程持有锁或恢复清理失败时释放已取得的锁并让启动失败。
    /// </summary>
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            serving = servingLock.Acquire();
            store.Recover(); await store.Cleanup(cancellationToken);
            await base.StartAsync(cancellationToken);
        }
        // 启动任何一步失败都要先释放已取得的服务锁再原样抛出，否则锁会被失败的进程一直占着，后续服务无法启动。
        catch { serving?.Dispose(); serving = null; throw; }
    }

    /// <summary>停止定时清理任务并释放服务锁；即使停止过程抛出异常也会释放锁。</summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    { try { await base.StopAsync(cancellationToken); } finally { /* 无论后台任务停止是否成功、是否被取消都释放服务锁，避免下一次启动拿不到锁。 */ serving?.Dispose(); serving = null; } }

    /// <summary>释放后台服务资源并释放仍持有的服务锁；可重复调用。</summary>
    public override void Dispose() { base.Dispose(); serving?.Dispose(); serving = null; }

    /// <summary>每小时（按用量服务的时钟）清理一次超出保留窗口的记录，直到服务停止；单次清理的存储故障只计入失败次数，不终止循环。</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), store.Clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { await store.Cleanup(stoppingToken); }
                // 只吞掉可恢复的存储故障并记入健康状态，让下一个小时继续重试；取消和其他异常照常向外传播。
                catch (Exception error) when (UsageService.StorageError(error)) { store.MaintenanceFailure(); }
            }
        }
        // 服务正常停止时的取消属于预期结束，静默返回；不是由停止令牌引起的取消仍然向外抛出。
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
