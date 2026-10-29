using Application.Interfaces.Identity;
using Core.Configuration;
using Core.Identity;

namespace Application.Services.Identity;

/// <summary>网页和 CLI 共用的使用者管理；明文令牌不进入持久化模型。</summary>
public sealed class IdentityService(IIdentityRepository repository, ClientRegistry clients) : IIdentityService
{
    private readonly object writes = new();
    /// <summary>中转鉴权使用的令牌名单快照；本服务每次成功写入后立即用仓储返回的已提交名单替换它。</summary>
    public ClientRegistry Clients { get; } = clients;

    /// <summary>列出全部使用者及其当前令牌版本，直接读仓储。</summary>
    public IReadOnlyList<RelayUser> List() => repository.List();

    /// <summary>
    /// 新建使用者并签发第一版个人令牌：显示名去首尾空白后须为 1–64 个字符且不含控制字符；
    /// 未指定 <paramref name="legacyName"/> 时自动生成 <c>u_</c> 加主键前 24 位的内部名称，旧客户端迁移时可沿用原名称但同样须合法。
    /// 返回结果中的明文令牌只出现这一次，库里只存其哈希。
    /// </summary>
    public IssuedToken Create(string displayName, string? legacyName = null)
    {
        displayName = ValidateDisplayName(displayName);
        var id = Guid.NewGuid().ToString("N"); var name = legacyName ?? "u_" + id[..24];
        if (!RelayConfiguration.IsClientName(name)) throw new InvalidOperationException("客户端名称只能是小写字母、数字、短横线或下划线，最多 32 字符。");
        var token = ConfigurationFiles.NewToken();
        return Change(() => (new IssuedToken(id, displayName, token), repository.Create(id, name, displayName, ConfigurationFiles.Hash(token))));
    }

    /// <summary>修改显示名（同样做长度与控制字符校验），令牌不变；使用者不存在时由仓储拒绝。</summary>
    public void Rename(string userId, string displayName)
    {
        displayName = ValidateDisplayName(displayName);
        Change(() => (0, repository.Rename(userId, displayName)));
    }

    /// <summary>启用或停用使用者；提交后立即发布新名单，本进程的鉴权马上生效。</summary>
    public void SetEnabled(string userId, bool enabled) => Change(() => (0, repository.SetEnabled(userId, enabled)));

    /// <summary>轮换个人令牌：生成新令牌并写入其哈希，旧令牌随之失效；返回的明文新令牌只出现这一次。</summary>
    public IssuedToken Rotate(string userId)
    {
        var token = ConfigurationFiles.NewToken();
        return Change(() =>
        {
            var rotated = repository.Rotate(userId, ConfigurationFiles.Hash(token));
            return (new IssuedToken(userId, rotated.DisplayName, token), rotated.Access);
        });
    }

    /// <summary>串行执行一次仓储写入，并用其返回的已提交名单替换鉴权快照，最后返回写入结果；写入或名单校验失败时异常上抛且不发布新名单。</summary>
    private T Change<T>(Func<(T Value, IReadOnlyList<ClientAccess> Permissions)> change)
    {
        // 同一服务提交后立即发布已验证快照；失败时不发布，跨进程鉴权还会重新读库。
        lock (writes)
        {
            var result = change();
            Clients.Replace(result.Permissions);
            return result.Value;
        }
    }
    /// <summary>从仓储读取已提交的鉴权名单（只含有效令牌的哈希），不经过内存快照。</summary>
    public IReadOnlyList<ClientAccess> ReadAccess() => repository.ReadAccess();

    /// <summary>去掉首尾空白后校验显示名为 1–64 个字符且不含控制字符，返回去空白后的值；不合法时抛出 <see cref="InvalidOperationException"/>。</summary>
    private static string ValidateDisplayName(string name)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 64 || name.Any(char.IsControl)) throw new InvalidOperationException("显示名应为 1 至 64 个字符，不包含控制字符。");
        return name;
    }
}
