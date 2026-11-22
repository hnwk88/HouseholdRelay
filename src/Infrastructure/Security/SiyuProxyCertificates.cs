using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Application.DependencyInjection;
using Application.Interfaces.Certificates;
using Application.Interfaces.Security;
using Application.Interfaces.Siyu;
using Core.Certificates;
using Core.Siyu;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.Security;

/// <summary>
/// 思域代理 CA 的产品实现：CA 连同私钥经 DPAPI 存进 secrets 表的 <see cref="ProtectedSecret.SiyuProxyAuthority"/>，和站点私有 CA 分开；
/// 没有或快到期时新建一张（带 Name Constraints，只许两个官方主机），每次建思域会话都把当前 CA 公钥交给 MyCoAI，所以换 CA 只影响新会话。
/// 叶证书只在内存里，按主机各一张，剩余有效期不足 <see cref="LeafRenewBefore"/> 时用同一个 CA 重签；换下来的旧叶证书可能还在握手中，留到释放时一起释放。
/// </summary>
internal sealed class SiyuProxyCertificates : ISiyuProxyCertificates, IDisposable
{
    /// <summary>CA 剩余有效期不足这么久就在启动时换新：叶证书 30 天，留足两轮重签。</summary>
    internal static readonly TimeSpan AuthorityRenewBefore = TimeSpan.FromDays(90);

    /// <summary>叶证书剩余有效期不足这么久就重签。</summary>
    internal static readonly TimeSpan LeafRenewBefore = TimeSpan.FromDays(7);

    private readonly X509Certificate2 _authority;
    private readonly X509Certificate2 _publicAuthority;
    private readonly ICertificateFactory _factory;
    private readonly TimeProvider _clock;
    private readonly object _sync = new();
    private readonly Dictionary<OfficialHost, X509Certificate2> _leaves = [];
    private readonly List<X509Certificate2> _retired = [];

    /// <summary>由 <see cref="Load"/> 构造；<paramref name="authority"/> 必须带私钥，所有权交给本实例。</summary>
    private SiyuProxyCertificates(X509Certificate2 authority, ICertificateFactory factory, TimeProvider clock)
    {
        _authority = authority; _factory = factory; _clock = clock;
        _publicAuthority = X509CertificateLoader.LoadCertificate(authority.RawData);
    }

    /// <summary>思域代理 CA 的公钥证书（不带私钥）。</summary>
    public X509Certificate2 Authority => _publicAuthority;

    /// <summary>
    /// 读出存着的代理 CA；没有、解不开、不是带私钥的 CA，或剩余有效期不足 <see cref="AuthorityRenewBefore"/> 时新建一张并覆盖存下。
    /// 解密出的 PFX 明文用完即清零。
    /// </summary>
    public static async Task<SiyuProxyCertificates> Load(IServiceScopeFactory scopes, ISecretProtector protector, ICertificateFactory factory, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(scopes); ArgumentNullException.ThrowIfNull(protector); ArgumentNullException.ThrowIfNull(factory); ArgumentNullException.ThrowIfNull(clock);
        await using var scope = scopes.CreateAsyncScope();
        var secrets = scope.Repository<ProtectedSecret>();
        var row = await secrets.FirstOrDefaultAsync(item => item.Name == ProtectedSecret.SiyuProxyAuthority);
        if (row is not null && Usable(Open(protector, row.Protected), clock) is { } stored) return new SiyuProxyCertificates(stored, factory, clock);
        var created = factory.CreateProxyAuthority([OfficialTargets.ApiHost, OfficialTargets.McpProxyHost]);
        var plain = created.Export(X509ContentType.Pkcs12);
        try
        {
            if (row is null) await secrets.AddAsync(row = new ProtectedSecret { Name = ProtectedSecret.SiyuProxyAuthority });
            row.Protected = protector.Protect(plain);
            await secrets.SaveChangesAsync();
        }
        // 保存失败时新建的 CA 不能留着用（下次启动会再建一张，客户端拿到的会对不上），释放后原样抛出。
        catch { created.Dispose(); throw; }
        // CA 的明文 PFX 含私钥，只用于加密存库。
        finally { CryptographicOperations.ZeroMemory(plain); }
        return new SiyuProxyCertificates(created, factory, clock);
    }

    /// <summary>给 <paramref name="host"/> 的带私钥叶证书；剩余有效期不足 <see cref="LeafRenewBefore"/> 时重签。</summary>
    public X509Certificate2 Leaf(OfficialHost host)
    {
        var name = OfficialTargets.Name(host);
        // 多条隧道会同时握手：查有效期与重签在同一把锁里，同一主机不会并发签出两张；签发只在本机算，不等网络。
        lock (_sync)
        {
            if (_leaves.TryGetValue(host, out var leaf) && leaf.NotAfter.ToUniversalTime() - _clock.GetUtcNow().UtcDateTime > LeafRenewBefore) return leaf;
            if (leaf is not null) _retired.Add(leaf);
            return _leaves[host] = _factory.IssueProxyLeaf(_authority, name);
        }
    }

    /// <summary>释放 CA 与全部叶证书（叶证书在用户密钥容器里的临时密钥随之删除）。</summary>
    public void Dispose()
    {
        // 与 Leaf 同一把锁：释放时不会有握手正好拿到一张刚被释放的叶证书。
        lock (_sync)
        {
            foreach (var leaf in _leaves.Values.Concat(_retired)) leaf.Dispose();
            _leaves.Clear(); _retired.Clear();
        }
        _publicAuthority.Dispose(); _authority.Dispose();
    }

    /// <summary>解开存着的 PFX；解不开（换了 Windows 用户、数据损坏）返回 null，由调用方新建。</summary>
    private static X509Certificate2? Open(ISecretProtector protector, byte[] encrypted)
    {
        byte[] plain;
        try { plain = protector.Unprotect(encrypted); }
        // DPAPI 解不开只说明这份密文在当前用户下不可用，新建一张即可；不把底层异常带出去。
        catch (CryptographicException) { return null; }
        try { return X509CertificateLoader.LoadPkcs12(plain, null, X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable); }
        // 解出来的不是合法 PFX：同样当作没有。
        catch (CryptographicException) { return null; }
        // 解密出的 PFX 含 CA 私钥，加载成败都立即清零。
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    /// <summary>带私钥、是 CA、带 Name Constraints 且剩余有效期够长才用；不合格的释放掉返回 null。</summary>
    private static X509Certificate2? Usable(X509Certificate2? authority, TimeProvider clock)
    {
        if (authority is null) return null;
        var isAuthority = authority.Extensions.OfType<X509BasicConstraintsExtension>().Any(extension => extension.CertificateAuthority);
        var constrained = authority.Extensions.Any(extension => extension.Oid?.Value == "2.5.29.30" && extension.Critical);
        if (authority.HasPrivateKey && isAuthority && constrained && authority.NotAfter.ToUniversalTime() - clock.GetUtcNow().UtcDateTime > AuthorityRenewBefore) return authority;
        authority.Dispose();
        return null;
    }
}
