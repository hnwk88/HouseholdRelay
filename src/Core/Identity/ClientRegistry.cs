using System.Security.Cryptography;
using Core.Configuration;
using Core.Relay;

namespace Core.Identity;

/// <summary>个人令牌鉴权快照；每次鉴权前可重读已提交名单，使其他进程的撤销立即生效。</summary>
public sealed class ClientRegistry
{
    private ClientAccess[] _clients;
    private readonly Func<Task<IReadOnlyList<ClientAccess>>>? _readCommitted;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    /// <summary>用初始名单建立快照（构造时不校验）；<paramref name="readCommitted"/> 为 null 时只用内存名单，不在鉴权前重读数据库。</summary>
    public ClientRegistry(IEnumerable<ClientAccess> values, Func<Task<IReadOnlyList<ClientAccess>>>? readCommitted = null)
    { _clients = values.ToArray(); this._readCommitted = readCommitted; }

    /// <summary>名单每次被替换或清空后通知的订阅者（思域据此关闭令牌已失效的会话）；只增不减，随单例活到进程结束。</summary>
    private Action<IReadOnlyList<ClientAccess>>[] _observers = [];

    /// <summary>校验后整体替换名单快照；校验失败时抛出异常且保留旧快照。以原子引用替换实现，读线程不会看到半更新的名单。替换后通知订阅者。</summary>
    public void Replace(IReadOnlyList<ClientAccess> values)
    {
        RelayConfiguration.ValidateClients(values);
        var snapshot = values.ToArray();
        Volatile.Write(ref _clients, snapshot);
        Notify(snapshot);
    }

    /// <summary>清空名单，使之后所有令牌都鉴权失败；用于数据库不可读时的失败即拒绝。清空后通知订阅者。</summary>
    public void DisableAll()
    {
        Volatile.Write(ref _clients, []);
        Notify([]);
    }

    /// <summary>
    /// 订阅名单变化：本进程提交后的替换、鉴权前重读到别的进程的改动、读库失败时的清空，都会同步回调一次当前名单。
    /// 回调要快、不等网络，不得抛出；它在替换方的线程上运行。
    /// </summary>
    public void Subscribe(Action<IReadOnlyList<ClientAccess>> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        // 订阅只在启动组装时发生，加锁保证并发订阅不丢项；通知读的是数组快照，不必持锁。
        lock (_observerLock) _observers = [.. _observers, observer];
    }

    /// <summary>名单中是否仍有这名使用者这一版启用中的令牌（按主键比较）。</summary>
    public bool IsCurrent(long userId, long tokenId) =>
        Volatile.Read(ref _clients).Any(c => c.Enabled && c.UserId == userId && c.TokenId == tokenId);

    /// <summary>把当前名单交给每个订阅者。</summary>
    private void Notify(IReadOnlyList<ClientAccess> snapshot)
    {
        foreach (var observer in Volatile.Read(ref _observers)) observer(snapshot);
    }

    private readonly object _observerLock = new();

    /// <summary>
    /// 按个人令牌鉴权：令牌为空或超过 512 字符直接返回 null；否则先重读已提交名单，再以定长比较查找启用中的匹配项，未命中返回 null。
    /// 重读失败时清空名单并抛出 503 的 <see cref="RelayFailure"/>。可被多个请求并发调用，重读过程串行执行。
    /// </summary>
    public async Task<ClientAccess?> AuthenticateAsync(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 512) return null;
        // 每个鉴权边界读取数据库已提交快照，使另一个 CLI 进程的撤销也立即生效。
        if (_readCommitted is not null)
        {
            await _refreshLock.WaitAsync();
            try
            {
                // 先读数据库已提交名单并校验，成功才替换快照；信号量保证同一时刻只有一个请求在刷新。
                try { Replace(await _readCommitted()); }
                // 读不到或读到非法名单时宁可拒绝全部请求也不沿用旧名单，防止已撤销的令牌在故障期间继续可用；捕获全部异常是为了任何故障都走同一失败即拒绝路径。
                catch { DisableAll(); throw new RelayFailure(503, "Access database unavailable."); }
            }
            // 刷新成功、失败即拒绝都要归还信号量，否则之后所有鉴权都会卡在等待刷新上。
            finally { _refreshLock.Release(); }
        }
        var hash = Convert.FromHexString(ConfigurationFiles.Hash(token));
        return Volatile.Read(ref _clients).FirstOrDefault(c => c.Enabled && CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(c.TokenHash)));
    }
}
