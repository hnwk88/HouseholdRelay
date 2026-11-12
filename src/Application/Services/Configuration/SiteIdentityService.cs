using Application.Interfaces.ClaudeCode;
using Application.Interfaces.Configuration;
using Core.Configuration;

namespace Application.Services.Configuration;

/// <summary>一站一份身份的维护流程：独立操作计数、限时排空、持久提交及可恢复对齐。</summary>
public sealed class SiteIdentityService(ISiteIdentityRepository repository, IClaudeConfigurationPreparation preparation, TimeProvider clock,
    IOwnedClaudeProcesses ownedProcesses) : ISiteIdentityService
{
    private readonly object sync = new();
    private readonly SemaphoreSlim maintenance = new(1, 1);
    private SiteIdentitySnapshot current = repository.Read();
    private TaskCompletionSource drained = Completed();
    private Task? aligning;
    private int active;
    private bool available;
    private string status = "尚未开始设备准备，官方账户暂不可用。";

    /// <summary>在同一锁下读取已发布身份，避免版本和编号读取错代。</summary>
    // 与维护发布共用同步锁，读取的是同一代完整快照。
    public SiteIdentitySnapshot Current { get { lock (sync) return current; } }
    /// <summary>在准入锁下读取准备结果，与维护关闭同步。</summary>
    // 准入检查与停止新操作的写入共用同步锁。
    public bool Available { get { lock (sync) return available; } }
    /// <summary>在状态锁下读取不含秘密的管理员进度。</summary>
    // 状态消息与准入变更一起发布，避免读屏获得过时状态。
    public string Status { get { lock (sync) return status; } }

    /// <summary>在同一锁内检查准入并计数，维护不能插入检查和登记之间。</summary>
    public IDisposable Enter()
    {
        // 状态与准入计数共用同一把锁，避免维护和正在结束的操作看到不一致状态。
        lock (sync)
        {
            if (!available) throw new SiteIdentityUnavailableException();
            if (active++ == 0) drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return new SiteIdentityPermit(Leave);
        }
    }

    /// <summary>归还一次完整操作许可，最后一个操作结束时唤醒维护等待者。</summary>
    private void Leave()
    {
        // 状态与准入计数共用同一把锁，避免维护和正在结束的操作看到不一致状态。
        lock (sync) { if (--active == 0) drained.TrySetResult(); }
    }

    /// <summary>服务排他锁已取得后串行准备，失败保留官方关闭。</summary>
    public async Task Prepare(CancellationToken cancellation)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(60), clock);
        // 主机取消和排空预算同时生效；既有操作只等待完成，不由准备流程强行取消。
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, budget.Token);
        var entered = false;
        try
        {
            await maintenance.WaitAsync(linked.Token); entered = true;
            await CloseAndDrain(linked.Token);
            await Align(cancellation);
        }
        // 对齐失败保留关闭，管理页和独立 API 仍可启动；不输出文件错误原文。
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
        { SetUnavailable(error is ClaudeConfigurationUnavailableException ? error.Message : "设备准备未完成，官方账户保持暂停。请修复本站私有配置后继续原操作。"); }
        // 所有退出路径都归还维护互斥，失败后的准入仍由持久状态决定。
        finally { if (entered) maintenance.Release(); }
    }

    /// <summary>串行执行维护并限时排空；提交后继续同一目标，重复操作不生成。</summary>
    public async Task<SiteIdentityOperationResult> Refresh(string operationId, long expectedVersion, CancellationToken cancellation)
    {
        if (!Guid.TryParseExact(operationId, "N", out _)) throw new InvalidOperationException("维护操作无效，请重新打开设备维护页面。");
        if (repository.Find(operationId) is { Pending: false } completed) return completed;
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(60), clock);
        // 管理员取消与本阶段时间预算合并，只取消等待或准备，不终止既有凭据写入。
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, budget.Token);
        var entered = false;
        var wasAvailable = false;
        var before = Current;
        try
        {
            await maintenance.WaitAsync(linked.Token); entered = true;
            wasAvailable = Available; before = Current;
            if (repository.Find(operationId) is { Pending: false } finished) return finished;
            await CloseAndDrain(linked.Token);
            ownedProcesses.EnsureExited();
            linked.Token.ThrowIfCancellationRequested();
            var result = repository.Begin(operationId, expectedVersion, linked.Token);
            // 状态与准入计数共用同一把锁，避免维护和正在结束的操作看到不一致状态。
            lock (sync) current = result.Current;
            if (result.Pending)
            {
                await Align(CancellationToken.None);
                return result with { Current = Current, Pending = false };
            }
            // 查询后被另一合法流程完成的操作也只返回历史结果，不再次提交ready。
            lock (sync) { available = wasAvailable && !result.Current.Pending; status = result.Current.Pending
                ? "先前操作已完成；当前仍有另一项设备维护，请继续当前操作。"
                : "这次设备维护已经完成，重复提交没有再次更换。"; }
            return result;
        }
        // 提交前排空或等锁超时不换号，亦不取消任何正在写凭据的 CLI。
        catch (OperationCanceledException) when (budget.IsCancellationRequested || cancellation.IsCancellationRequested)
        {
            if (entered) RestoreAfterFailure(wasAvailable, before);
            throw new InvalidOperationException("等待超时或操作已取消，本次没有提交更换。请查看当前维护状态后继续原操作。");
        }
        // 仅专用安全异常的固定原因可交给管理员；其他底层异常原文不进入状态。
        catch (Exception error)
        {
            if (entered) RestoreAfterFailure(wasAvailable, before);
            if (error is ClaudeConfigurationUnavailableException) SetUnavailable(error.Message);
            throw;
        }
        // 所有退出路径都归还维护互斥，失败后的准入仍由持久状态决定。
        finally { if (entered) maintenance.Release(); }
    }

    /// <summary>串行恢复同一个持久目标，不与另一维护交叉更新文件。</summary>
    public async Task<SiteIdentityOperationResult> Resume(string operationId, CancellationToken cancellation)
    {
        if (!Guid.TryParseExact(operationId, "N", out _)) throw new InvalidOperationException("维护操作无效，请重新打开设备维护页面。");
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(60), clock);
        // 恢复同样把等待互斥和已有操作计算在排空预算内，不强杀任何 CLI。
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, budget.Token);
        var entered = false;
        var wasAvailable = false;
        var before = Current;
        try
        {
            await maintenance.WaitAsync(linked.Token); entered = true;
            wasAvailable = Available; before = Current;
            var operation = repository.Find(operationId) ?? throw new InvalidOperationException("设备维护操作不存在，请重新加载页面。");
            var snapshot = operation.Current;
            if (snapshot.Pending && snapshot.OperationId != operationId) throw new InvalidOperationException("页面已过期，请重新打开并继续当前设备维护。");
            if (!snapshot.Pending && wasAvailable) return operation;
            await CloseAndDrain(linked.Token);
            // 状态与准入计数共用同一把锁，避免维护和正在结束的操作看到不一致状态。
            lock (sync) current = snapshot;
            await Align(cancellation);
            return operation with { Current = Current, Pending = false };
        }
        // 恢复排空失败保持既有 pending；尚未持久更换的站点只恢复原本已就绪的准入。
        catch (OperationCanceledException) when (budget.IsCancellationRequested || cancellation.IsCancellationRequested)
        {
            if (entered) RestoreAfterFailure(wasAvailable, before);
            throw new InvalidOperationException("等待超时或操作已取消，更换尚未完成。请等待已有官方操作结束后继续原操作。");
        }
        // 读取或准备失败只根据持久状态恢复；专用安全原因可保留，其他原文不展示。
        catch (Exception error)
        {
            if (entered) RestoreAfterFailure(wasAvailable, before);
            if (error is ClaudeConfigurationUnavailableException) SetUnavailable(error.Message);
            throw;
        }
        // 所有退出路径都归还维护互斥，失败后的准入仍由持久状态决定。
        finally { if (entered) maintenance.Release(); }
    }

    /// <summary>关闭新准入后取得既有完整操作的完成信号，直到全部归还才允许准备文件。</summary>
    private async Task CloseAndDrain(CancellationToken cancellation)
    {
        Task waiting;
        // 关闭与取得排空任务必须同时完成，新许可不能插入两者之间。
        lock (sync)
        {
            available = false;
            status = current.Pending ? "设备更换尚未完成，正在等待已有操作结束后继续。" : "正在等待已有官方调用和登录程序结束，尚未更换设备编号。";
            waiting = drained.Task;
        }
        await waiting.WaitAsync(cancellation);
        cancellation.ThrowIfCancellationRequested();
    }

    /// <summary>同一个目标的派生文件全部就绪后发布快照，最后清除 pending；超时保持关闭。</summary>
    private async Task Align(CancellationToken cancellation)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30), clock);
        // 管理员取消与本阶段时间预算合并，只取消等待或准备，不终止既有凭据写入。
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, budget.Token);
        // 状态与准入计数共用同一把锁，避免维护和正在结束的操作看到不一致状态。
        lock (sync) { available = false; status = "设备更换尚未完成，正在准备本站全部官方账户。"; }
        try
        {
            if (aligning is not null)
            {
                try { await aligning.WaitAsync(linked.Token); }
                // 前一次后台准备已退出后允许重试；超时或取消仍继续保持关闭，不能并行覆盖文件。
                catch (Exception) when (aligning.IsCompleted && !linked.IsCancellationRequested) { }
                aligning = null;
            }
            linked.Token.ThrowIfCancellationRequested();
            var target = repository.Read();
            // 文件准备持有独立任务；超时不提前发布 ready，继续操作先等旧任务退出。
            aligning = Task.Run(() => preparation.PrepareExistingAccounts(linked.Token), CancellationToken.None);
            await aligning.WaitAsync(linked.Token);
            aligning = null;
            linked.Token.ThrowIfCancellationRequested();
            var snapshot = repository.Read();
            if (snapshot != target) throw new InvalidOperationException("准备期间站点身份发生变化，请继续当前维护操作。");
            linked.Token.ThrowIfCancellationRequested();
            // 状态与准入计数共用同一把锁，避免维护和正在结束的操作看到不一致状态。
            lock (sync) current = snapshot;
            if (snapshot.Pending) snapshot = repository.Complete(snapshot.OperationId!, linked.Token).Current;
            // ready可能已持久提交才到截止时间；保留真实快照，但取消时仍先关闭，继续准备后才重新开放。
            lock (sync) current = snapshot;
            linked.Token.ThrowIfCancellationRequested();
            // 状态与准入计数共用同一把锁，避免维护和正在结束的操作看到不一致状态。
            lock (sync) { current = snapshot; available = true; status = "设备准备已完成，官方账户可以继续使用。账户本身的暂停与限制保持原状。"; }
        }
        // 只保留经过固定读句约束的配置异常，其余错误统一描述，不泄露文件内容或目录。
        catch (Exception error)
        {
            if (error is ClaudeConfigurationUnavailableException)
            {
                var message = "设备更换或准备未完成，官方账户保持暂停。" + error.Message;
                SetUnavailable(message); throw new ClaudeConfigurationUnavailableException(message);
            }
            SetUnavailable("设备更换或准备未完成，官方账户保持暂停。请修复本站私有配置后继续原操作。");
            throw new InvalidOperationException("设备准备未完成。已提交的更换会保留，修复本站私有配置后请继续原操作；独立 API 仍可使用。");
        }
    }

    /// <summary>失败后以持久状态决定是否恢复旧准入，绝不把已提交的 pending 伪装成 ready。</summary>
    private void RestoreAfterFailure(bool wasAvailable, SiteIdentitySnapshot before)
    {
        SetUnavailable("设备状态未确认，官方账户保持暂停。请修复本站配置后继续原操作。");
        var snapshot = repository.Read();
        // 状态与准入计数共用同一把锁，避免维护和正在结束的操作看到不一致状态。
        lock (sync)
        {
            current = snapshot; available = !snapshot.Pending && wasAvailable && snapshot == before;
            status = snapshot.Pending ? "设备更换未完成，请继续原操作；官方账户保持暂停。"
                : snapshot != before ? "设备编号已保存，准备结果尚待确认；请继续原操作，官方账户保持暂停。"
                : "设备编号未更换。已有官方操作可继续，请稍后重试。";
        }
    }

    /// <summary>先关闭准入再读取存储或报告失败，数据库读取异常也不能留下开放窗口。</summary>
    private void SetUnavailable(string message)
    {
        // 准入和读屏消息同锁发布，存储故障期间也保留完整关闭状态。
        lock (sync) { available = false; status = message; }
    }

    /// <summary>没有活跃操作时使用已完成信号，避免维护无谓等待。</summary>
    private static TaskCompletionSource Completed()
    { var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); signal.SetResult(); return signal; }
}
