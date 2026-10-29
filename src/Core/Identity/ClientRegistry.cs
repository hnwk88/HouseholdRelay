using System.Security.Cryptography;
using Core.Configuration;
using Core.Relay;

namespace Core.Identity;

/// <summary>个人令牌鉴权快照；每次鉴权前可重读已提交名单，使其他进程的撤销立即生效。</summary>
public sealed class ClientRegistry
{
    private ClientAccess[] clients;
    private readonly Func<IReadOnlyList<ClientAccess>>? readCommitted;
    private readonly object refreshLock = new();
    /// <summary>用初始名单建立快照（构造时不校验）；<paramref name="readCommitted"/> 为 null 时只用内存名单，不在鉴权前重读数据库。</summary>
    public ClientRegistry(IEnumerable<ClientAccess> values, Func<IReadOnlyList<ClientAccess>>? readCommitted = null)
    { clients = values.ToArray(); this.readCommitted = readCommitted; }

    /// <summary>校验后整体替换名单快照；校验失败时抛出异常且保留旧快照。以原子引用替换实现，读线程不会看到半更新的名单。</summary>
    public void Replace(IReadOnlyList<ClientAccess> values)
    {
        RelayConfiguration.ValidateClients(values); Volatile.Write(ref clients, values.ToArray());
    }

    /// <summary>清空名单，使之后所有令牌都鉴权失败；用于数据库不可读时的失败即拒绝。</summary>
    public void DisableAll() => Volatile.Write(ref clients, []);

    /// <summary>
    /// 按个人令牌鉴权：令牌为空或超过 512 字符直接返回 null；否则先重读已提交名单，再以定长比较查找启用中的匹配项，未命中返回 null。
    /// 重读失败时清空名单并抛出 503 的 <see cref="RelayFailure"/>。可被多个请求线程并发调用，重读过程串行执行。
    /// </summary>
    public ClientAccess? Authenticate(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 512) return null;
        // 每个鉴权边界读取数据库已提交快照，使另一个 CLI 进程的撤销也立即生效。
        lock (refreshLock)
        {
            if (readCommitted is not null)
            {
                // 先读数据库已提交名单并校验，成功才替换快照；锁保证同一时刻只有一个线程在刷新。
                try { Replace(readCommitted()); }
                // 读不到或读到非法名单时宁可拒绝全部请求也不沿用旧名单，防止已撤销的令牌在故障期间继续可用；捕获全部异常是为了任何故障都走同一失败即拒绝路径。
                catch { DisableAll(); throw new RelayFailure(503, "Access database unavailable."); }
            }
        }
        var hash = Convert.FromHexString(ConfigurationFiles.Hash(token));
        return Volatile.Read(ref clients).FirstOrDefault(c => c.Enabled && CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(c.TokenHash)));
    }
}
