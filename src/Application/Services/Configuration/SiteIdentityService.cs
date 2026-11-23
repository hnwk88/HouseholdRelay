using Application.DependencyInjection;
using Application.Interfaces.ClaudeCode;
using Application.Interfaces.Configuration;
using Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Repositories;

namespace Application.Services.Configuration;

/// <summary>
/// 一站一份身份的维护流程：独立操作计数、限时排空、持久提交及可恢复对齐。
/// 身份状态（site_identity）、维护操作记录（site_identity_operations）与设置里的设备编号经通用仓储存取；只有 <see cref="BeginAsync"/> 能经唯一的生成入口换号，历史重试都只读已有结果。
/// </summary>
public sealed class SiteIdentityService(IServiceScopeFactory scopes, IDeviceIdGenerator deviceIds, IClaudeConfigurationPreparation preparation, TimeProvider clock,
    IOwnedClaudeProcesses ownedProcesses, IStorageBusy storage) : ISiteIdentityService
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _maintenance = new(1, 1);
    /// <summary>同一进程里的身份写入先可取消地排队，再进库；跨进程由写事务的写锁排队。</summary>
    private readonly SemaphoreSlim _identityWrites = new(1, 1);
    /// <summary>查操作与写入等写锁的上限（与改造前的 1 秒连接相同）：另一进程占着写锁时很快读出「库正忙」，不越过维护截止时间。</summary>
    private static readonly TimeSpan _lockWait = TimeSpan.FromSeconds(1);
    // 依赖注入构造是同步合同，初始快照只能在这里同步等待一次；Microsoft.Data.Sqlite 的异步读在内部同步完成，没有同步上下文，不会死锁。
    private SiteIdentitySnapshot _current = ReadIdentityAsync(scopes).GetAwaiter().GetResult();
    private TaskCompletionSource _drained = Completed();
    private Task? _aligning;
    private int _active;
    private bool _available;
    // 取消源只取消不释放：不带计时器，不占非托管资源；释放了反而会让晚到的读取撞上已释放的对象。
    /// <summary>这一轮准入的思域传输取消源；关闭准入时换代并在锁外取消旧的。</summary>
    private CancellationTokenSource _transport = new();
    private string _status = "尚未开始设备准备，官方账户暂不可用。";

    /// <summary>在同一锁下读取已发布身份，避免版本和编号读取错代。</summary>
    // 与维护发布共用同步锁，读取的是同一代完整快照。
    public SiteIdentitySnapshot Current { get { lock (_sync) return _current; } }
    /// <summary>在准入锁下读取准备结果，与维护关闭同步。</summary>
    // 准入检查与停止新操作的写入共用同步锁。
    public bool Available { get { lock (_sync) return _available; } }
    /// <summary>在状态锁下读取不含秘密的管理员进度。</summary>
    // 状态消息与准入变更一起发布，避免读屏获得过时状态。
    public string Status { get { lock (_sync) return _status; } }

    /// <summary>这一轮准入的思域传输停止信号。</summary>
    // 与关闭准入同锁读取，读到的不会是换代到一半的取消源。
    public CancellationToken TransportStopping { get { lock (_sync) return _transport.Token; } }

    /// <summary>在同一锁内检查准入并计数，维护不能插入检查和登记之间。</summary>
    public IDisposable Enter()
    {
        // 状态与准入计数共用同一把锁，避免维护和正在结束的操作看到不一致状态。
        lock (_sync)
        {
            if (!_available) throw new SiteIdentityUnavailableException();
            if (_active++ == 0) _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return new SiteIdentityPermit(Leave);
        }
    }

    /// <summary>归还一次完整操作许可，最后一个操作结束时唤醒维护等待者。</summary>
    private void Leave()
    {
        // 状态与准入计数共用同一把锁，避免维护和正在结束的操作看到不一致状态。
        lock (_sync) { if (--_active == 0) _drained.TrySetResult(); }
    }

    /// <summary>服务排他锁已取得后串行准备，失败保留官方关闭。</summary>
    public async Task PrepareAsync(CancellationToken cancellation)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(60), clock);
        // 主机取消和排空预算同时生效；既有操作只等待完成，不由准备流程强行取消。
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, budget.Token);
        var entered = false;
        try
        {
            await _maintenance.WaitAsync(linked.Token); entered = true;
            await CloseAndDrainAsync(linked.Token);
            await AlignAsync(cancellation);
        }
        // 对齐失败保留关闭，管理页和独立 API 仍可启动；不输出文件错误原文。
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
        { SetUnavailable(error is ClaudeConfigurationUnavailableException ? error.Message : "设备准备未完成，官方账户保持暂停。请修复本站私有配置后继续原操作。"); }
        // 所有退出路径都归还维护互斥，失败后的准入仍由持久状态决定。
        finally { if (entered) _maintenance.Release(); }
    }

    /// <summary>串行执行维护并限时排空；提交后继续同一目标，重复操作不生成。</summary>
    public async Task<SiteIdentityOperationResult> RefreshAsync(string operationId, long expectedVersion, CancellationToken cancellation)
    {
        if (!Guid.TryParseExact(operationId, "N", out _)) throw new InvalidOperationException("维护操作无效，请重新打开设备维护页面。");
        if (await FindAsync(operationId) is { Pending: false } completed) return completed;
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(60), clock);
        // 管理员取消与本阶段时间预算合并，只取消等待或准备，不终止既有凭据写入。
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, budget.Token);
        var entered = false;
        var wasAvailable = false;
        var before = Current;
        try
        {
            await _maintenance.WaitAsync(linked.Token); entered = true;
            wasAvailable = Available; before = Current;
            if (await FindAsync(operationId) is { Pending: false } finished) return finished;
            await CloseAndDrainAsync(linked.Token);
            ownedProcesses.EnsureExited();
            linked.Token.ThrowIfCancellationRequested();
            var result = await BeginAsync(operationId, expectedVersion, linked.Token);
            // 状态与准入计数共用同一把锁，避免维护和正在结束的操作看到不一致状态。
            lock (_sync) _current = result.Current;
            if (result.Pending)
            {
                await AlignAsync(CancellationToken.None);
                return result with { Current = Current, Pending = false };
            }
            // 查询后被另一合法流程完成的操作也只返回历史结果，不再次提交ready。
            lock (_sync) { _available = wasAvailable && !result.Current.Pending; _status = result.Current.Pending
                ? "先前操作已完成；当前仍有另一项设备维护，请继续当前操作。"
                : "这次设备维护已经完成，重复提交没有再次更换。"; }
            return result;
        }
        // 提交前排空或等锁超时不换号，亦不取消任何正在写凭据的 CLI。
        catch (OperationCanceledException) when (budget.IsCancellationRequested || cancellation.IsCancellationRequested)
        {
            if (entered) await RestoreAfterFailureAsync(wasAvailable, before);
            throw new InvalidOperationException("等待超时或操作已取消，本次没有提交更换。请查看当前维护状态后继续原操作。");
        }
        // 仅专用安全异常的固定原因可交给管理员；其他底层异常原文不进入状态。
        catch (Exception error)
        {
            if (entered) await RestoreAfterFailureAsync(wasAvailable, before);
            if (error is ClaudeConfigurationUnavailableException) SetUnavailable(error.Message);
            throw;
        }
        // 所有退出路径都归还维护互斥，失败后的准入仍由持久状态决定。
        finally { if (entered) _maintenance.Release(); }
    }

    /// <summary>串行恢复同一个持久目标，不与另一维护交叉更新文件。</summary>
    public async Task<SiteIdentityOperationResult> ResumeAsync(string operationId, CancellationToken cancellation)
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
            await _maintenance.WaitAsync(linked.Token); entered = true;
            wasAvailable = Available; before = Current;
            var operation = await FindAsync(operationId) ?? throw new InvalidOperationException("设备维护操作不存在，请重新加载页面。");
            var snapshot = operation.Current;
            if (snapshot.Pending && snapshot.OperationId != operationId) throw new InvalidOperationException("页面已过期，请重新打开并继续当前设备维护。");
            if (!snapshot.Pending && wasAvailable) return operation;
            await CloseAndDrainAsync(linked.Token);
            // 状态与准入计数共用同一把锁，避免维护和正在结束的操作看到不一致状态。
            lock (_sync) _current = snapshot;
            await AlignAsync(cancellation);
            return operation with { Current = Current, Pending = false };
        }
        // 恢复排空失败保持既有 pending；尚未持久更换的站点只恢复原本已就绪的准入。
        catch (OperationCanceledException) when (budget.IsCancellationRequested || cancellation.IsCancellationRequested)
        {
            if (entered) await RestoreAfterFailureAsync(wasAvailable, before);
            throw new InvalidOperationException("等待超时或操作已取消，更换尚未完成。请等待已有官方操作结束后继续原操作。");
        }
        // 读取或准备失败只根据持久状态恢复；专用安全原因可保留，其他原文不展示。
        catch (Exception error)
        {
            if (entered) await RestoreAfterFailureAsync(wasAvailable, before);
            if (error is ClaudeConfigurationUnavailableException) SetUnavailable(error.Message);
            throw;
        }
        // 所有退出路径都归还维护互斥，失败后的准入仍由持久状态决定。
        finally { if (entered) _maintenance.Release(); }
    }

    /// <summary>关闭新准入后取得既有完整操作的完成信号，直到全部归还才允许准备文件。</summary>
    private async Task CloseAndDrainAsync(CancellationToken cancellation)
    {
        Task waiting; CancellationTokenSource stopping;
        // 关闭与取得排空任务必须同时完成，新许可不能插入两者之间。
        lock (_sync)
        {
            _available = false;
            _status = _current.Pending ? "设备更换尚未完成，正在等待已有操作结束后继续。" : "正在等待已有官方调用和登录程序结束，尚未更换设备编号。";
            waiting = _drained.Task;
            stopping = RotateTransport();
        }
        // 锁外取消思域传输：长流和空闲隧道先收尾、归还许可，排空才不会被它们拖满时限；正式 CLI 不受影响，照旧等它安全退出。
        stopping.Cancel();
        await waiting.WaitAsync(cancellation);
        cancellation.ThrowIfCancellationRequested();
    }

    /// <summary>同一个目标的派生文件全部就绪后发布快照，最后清除 pending；超时保持关闭。</summary>
    private async Task AlignAsync(CancellationToken cancellation)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30), clock);
        // 管理员取消与本阶段时间预算合并，只取消等待或准备，不终止既有凭据写入。
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, budget.Token);
        // 状态与准入计数共用同一把锁，避免维护和正在结束的操作看到不一致状态。
        CancellationTokenSource stopping;
        lock (_sync) { _available = false; _status = "设备更换尚未完成，正在准备本站全部官方账户。"; stopping = RotateTransport(); }
        // 锁外取消，理由同 CloseAndDrain。
        stopping.Cancel();
        try
        {
            if (_aligning is not null)
            {
                try { await _aligning.WaitAsync(linked.Token); }
                // 前一次后台准备已退出后允许重试；超时或取消仍继续保持关闭，不能并行覆盖文件。
                catch (Exception) when (_aligning.IsCompleted && !linked.IsCancellationRequested) { }
                _aligning = null;
            }
            linked.Token.ThrowIfCancellationRequested();
            var target = await ReadIdentityAsync(scopes);
            // 文件准备持有独立任务；超时不提前发布 ready，继续操作先等旧任务退出。
            _aligning = Task.Run(() => preparation.PrepareExistingAccounts(linked.Token), CancellationToken.None);
            await _aligning.WaitAsync(linked.Token);
            _aligning = null;
            linked.Token.ThrowIfCancellationRequested();
            var snapshot = await ReadIdentityAsync(scopes);
            if (snapshot != target) throw new InvalidOperationException("准备期间站点身份发生变化，请继续当前维护操作。");
            linked.Token.ThrowIfCancellationRequested();
            // 状态与准入计数共用同一把锁，避免维护和正在结束的操作看到不一致状态。
            lock (_sync) _current = snapshot;
            if (snapshot.Pending) snapshot = (await CompleteAsync(snapshot.OperationId!, linked.Token)).Current;
            // ready可能已持久提交才到截止时间；保留真实快照，但取消时仍先关闭，继续准备后才重新开放。
            lock (_sync) _current = snapshot;
            linked.Token.ThrowIfCancellationRequested();
            // 状态与准入计数共用同一把锁，避免维护和正在结束的操作看到不一致状态。
            lock (_sync) { _current = snapshot; _available = true; _status = "设备准备已完成，官方账户可以继续使用。账户本身的暂停与限制保持原状。"; }
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
    private async Task RestoreAfterFailureAsync(bool wasAvailable, SiteIdentitySnapshot before)
    {
        SetUnavailable("设备状态未确认，官方账户保持暂停。请修复本站配置后继续原操作。");
        var snapshot = await ReadIdentityAsync(scopes);
        // 状态与准入计数共用同一把锁，避免维护和正在结束的操作看到不一致状态。
        lock (_sync)
        {
            _current = snapshot; _available = !snapshot.Pending && wasAvailable && snapshot == before;
            _status = snapshot.Pending ? "设备更换未完成，请继续原操作；官方账户保持暂停。"
                : snapshot != before ? "设备编号已保存，准备结果尚待确认；请继续原操作，官方账户保持暂停。"
                : "设备编号未更换。已有官方操作可继续，请稍后重试。";
        }
    }

    /// <summary>先关闭准入再读取存储或报告失败，数据库读取异常也不能留下开放窗口。</summary>
    private void SetUnavailable(string message)
    {
        CancellationTokenSource stopping;
        // 准入和读屏消息同锁发布，存储故障期间也保留完整关闭状态。
        lock (_sync) { _available = false; _status = message; stopping = RotateTransport(); }
        // 锁外取消：取消回调会同步关闭思域隧道，不能占着准入锁。
        stopping.Cancel();
    }

    /// <summary>关闭准入时调用（须在 <see cref="_sync"/> 内）：换上下一轮的传输令牌，返回上一轮的取消源交给调用方在锁外取消。</summary>
    private CancellationTokenSource RotateTransport()
    {
        var previous = _transport;
        _transport = new CancellationTokenSource();
        return previous;
    }

    /// <summary>读取 canonical 编号（只从设置取）及对应代次并交叉校验；只读，不抢写锁，别人占着写锁时照样读得到（见 <see cref="ReadCurrentAsync"/> 的双检）。</summary>
    private static async Task<SiteIdentitySnapshot> ReadIdentityAsync(IServiceScopeFactory scopes)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await ReadCurrentAsync(scope);
    }

    /// <summary>读取当前持久身份（不经内存快照）；测试用它核对库里的身份。</summary>
    internal Task<SiteIdentitySnapshot> ReadAsync() => ReadIdentityAsync(scopes);

    /// <summary>只读查询历史操作及当前身份；未找到返回 null，不生成编号。</summary>
    internal async Task<SiteIdentityOperationResult?> FindAsync(string operationId)
    {
        operationId = CheckOperation(operationId);
        await using var scope = scopes.CreateAsyncScope();
        var states = scope.Repository<SiteIdentityState>();
        // 只读查询不开写事务（SQLite 下写事务是立即写锁），也不抢写锁；等锁上限仍是 1 秒，与改造前的 1 秒连接相同。
        return await states.WithLockWaitAsync(_lockWait, async () => await ReadOperationAsync(scope, operationId, await ReadCurrentAsync(scope)));
    }

    /// <summary>排空之后调用；事务的写锁覆盖幂等检查和生成，竞争请求不会各自产生新号。只有这里经唯一的生成入口换号。</summary>
    internal async Task<SiteIdentityOperationResult> BeginAsync(string operationId, long expectedVersion, CancellationToken cancellation = default)
    {
        operationId = CheckOperation(operationId);
        return await WriteIdentityAsync(cancellation, "站点配置正由另一项操作保存，设备编号未更换。请稍后继续原操作。", async scope =>
        {
            var current = await ReadCurrentAsync(scope);
            if (await ReadOperationAsync(scope, operationId, current) is { } repeated) return repeated;
            if (current.Pending) throw new InvalidOperationException("已有设备维护尚未完成，请继续原操作。");
            if (current.Version != expectedVersion) throw new InvalidOperationException("站点身份版本已变化，请重新加载页面。");
            var settings = await scope.Repository<RelaySettings>().LoadSettingsAsync();
            cancellation.ThrowIfCancellationRequested();
            var generated = deviceIds.Generate();
            if (!SiteDeviceIdentity.IsValid(generated) || StringComparer.Ordinal.Equals(generated, current.DeviceId))
                throw new InvalidOperationException("无法生成新的站点设备编号，原编号保持不变。");
            var next = checked(current.Version + 1);
            settings.ReplaceDeviceId(generated);
            var operations = scope.Repository<SiteIdentityOperation>();
            var operation = new SiteIdentityOperation { OperationId = operationId, Version = next, Pending = true };
            await operations.AddAsync(operation);
            // 先提交操作记录拿到库生成的主键，身份状态再按它关联；两次提交在同一个事务里。
            await operations.SaveChangesAsync();
            var state = await LoadStateAsync(scope);
            state.Version = next; state.SiteIdentityOperationId = operation.Id; state.Pending = true;
            await operations.SaveChangesAsync();
            cancellation.ThrowIfCancellationRequested();
            return new SiteIdentityOperationResult(new SiteIdentitySnapshot(generated, next, operationId, true), operationId, next, true);
        });
    }

    /// <summary>仅当前未完成操作可转 ready；旧操作的完成重试不改变后续代次。</summary>
    internal async Task<SiteIdentityOperationResult> CompleteAsync(string operationId, CancellationToken cancellation = default)
    {
        operationId = CheckOperation(operationId);
        return await WriteIdentityAsync(cancellation, "站点配置正由另一项操作保存，更换尚未完成。请稍后继续原操作。", async scope =>
        {
            var current = await ReadCurrentAsync(scope);
            var operation = await ReadOperationAsync(scope, operationId, current) ?? throw new InvalidOperationException("设备维护操作不存在，请重新加载页面。");
            if (!operation.Pending) return operation;
            if (current.OperationId != operationId || current.Version != operation.Version || !current.Pending)
                throw new InvalidOperationException("设备维护记录不一致，请恢复配套备份。");
            var operations = scope.Repository<SiteIdentityOperation>();
            (await operations.FirstOrDefaultAsync(item => item.OperationId == operationId))!.Pending = false;
            (await LoadStateAsync(scope)).Pending = false;
            await operations.SaveChangesAsync();
            return operation with { Current = current with { Pending = false }, Pending = false };
        });
    }

    /// <summary>
    /// 同一进程的重复提交先可取消地排队；进库后在一个写事务里执行，提交前后都查取消，生成与 ready 提交都不能在预算过期后继续。
    /// 另一进程正占着写锁、等不到时读出 <paramref name="busyMessage"/>；未提交的事务随作用域释放回滚。
    /// </summary>
    private async Task<T> WriteIdentityAsync<T>(CancellationToken cancellation, string busyMessage, Func<AsyncServiceScope, Task<T>> action)
    {
        await _identityWrites.WaitAsync(cancellation);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            await using var scope = scopes.CreateAsyncScope();
            var states = scope.Repository<SiteIdentityState>();
            try { return await states.WithLockWaitAsync(_lockWait, () => states.ExecuteInTransactionAsync(() => action(scope))); }
            // 只把「库正忙」（另一进程占着写锁）读成固定读句；其他存储故障原样上抛。
            catch (Exception error) when (storage.IsBusy(error))
            { throw new InvalidOperationException(busyMessage); }
        }
        // 成功、数据库忙、取消或故障都归还进程内排队名额。
        finally { _identityWrites.Release(); }
    }

    /// <summary>
    /// 读取并交叉校验当前记录和操作历史；唯一编号仅从设置取出。只读、不抢写锁：用最新只读快照先读身份状态，再读操作记录与设置，最后再读一次身份状态核对行版本——
    /// 换号与完成都会改身份状态那一行，两次行版本相同就说明中间没有提交，拼出来的不会跨提交；不同就重读，连续三次都在变时读出「正在更换」。
    /// 在写事务里调用时读的是本事务看到的数据，双检自然一次通过。
    /// </summary>
    private static async Task<SiteIdentitySnapshot> ReadCurrentAsync(AsyncServiceScope scope)
    {
        var states = scope.Repository<SiteIdentityState>();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var state = await states.GetByIdSnapshotAsync(SiteIdentityState.SingletonId) ?? throw new InvalidOperationException("站点身份状态缺失，请恢复配套备份。");
            var operation = state.SiteIdentityOperationId is { } id ? await scope.Repository<SiteIdentityOperation>().GetByIdSnapshotAsync(id) : null;
            var settings = (await scope.Repository<RelaySettings>().GetByIdSnapshotAsync(RelaySettings.SingletonId)
                ?? throw new InvalidOperationException("实例配置缺失，请恢复配套备份。")).Snapshot();
            var again = await states.GetByIdSnapshotAsync(SiteIdentityState.SingletonId);
            if (again?.RowVersion is not { } version || state.RowVersion is not { } first || !version.AsSpan().SequenceEqual(first)) continue;
            if (state.Version < 1 || (state.SiteIdentityOperationId is null ? state.Version != 1 || state.Pending
                : operation is null || operation.Version != state.Version || operation.Pending != state.Pending))
                throw new InvalidOperationException("站点身份状态损坏，请恢复配套备份。");
            return new(settings.Value.DeviceId, state.Version, operation?.OperationId, state.Pending);
        }
        throw new InvalidOperationException("站点身份正在更换，请稍后重新加载页面。");
    }

    /// <summary>取出身份状态那一行；缺失时提示恢复配套备份。</summary>
    private static async Task<SiteIdentityState> LoadStateAsync(AsyncServiceScope scope) =>
        await scope.Repository<SiteIdentityState>().GetByIdAsync(SiteIdentityState.SingletonId) ?? throw new InvalidOperationException("站点身份状态缺失，请恢复配套备份。");

    /// <summary>读取操作历史状态，不读取或保存历史设备编号。</summary>
    private static async Task<SiteIdentityOperationResult?> ReadOperationAsync(AsyncServiceScope scope, string operationId, SiteIdentitySnapshot current) =>
        await scope.Repository<SiteIdentityOperation>().FirstOrDefaultAsync(item => item.OperationId == operationId) is { } operation
            ? new(current, operationId, operation.Version, operation.Pending) : null;

    /// <summary>只接受 GUID 操作编号并统一书写形式，避免同一编号因大小写或短横线重复登记。</summary>
    private static string CheckOperation(string operationId) => Guid.TryParseExact(operationId, "N", out var id)
        || Guid.TryParseExact(operationId, "D", out id) ? id.ToString("N")
        : throw new InvalidOperationException("设备维护操作编号不正确，请重新加载页面。");

    /// <summary>没有活跃操作时使用已完成信号，避免维护无谓等待。</summary>
    private static TaskCompletionSource Completed()
    { var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); signal.SetResult(); return signal; }
}
