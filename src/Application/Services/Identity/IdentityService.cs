using System.Security.Cryptography;
using Application.DependencyInjection;
using Application.Interfaces.Identity;
using Core.Configuration;
using Core.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Application.Services.Identity;

/// <summary>网页和 CLI 共用的使用者管理；明文令牌不进入持久化模型。数据经通用仓储存取，每次写入一个作用域、一个事务。</summary>
public sealed class IdentityService(IServiceScopeFactory scopes, ClientRegistry clients) : IIdentityService
{
    private readonly SemaphoreSlim _writes = new(1, 1);
    /// <summary>中转鉴权使用的令牌名单快照；本服务每次成功写入后立即用同一事务里读回并校验过的名单替换它。</summary>
    public ClientRegistry Clients { get; } = clients;

    /// <summary>按创建顺序（主键）列出有当前令牌的使用者及其令牌版本。</summary>
    public async Task<IReadOnlyList<RelayUser>> List()
    {
        await using var scope = scopes.CreateAsyncScope();
        var users = await scope.Repository<User>().GetAllAsync();
        var tokens = (await scope.Repository<Token>().FindAsync(item => item.RevokedAt == null)).ToDictionary(item => item.UserId);
        return users.OrderBy(item => item.Id).Where(item => tokens.ContainsKey(item.Id))
            .Select(item => new RelayUser(item.Id, item.Name, item.DisplayName, item.Enabled, tokens[item.Id].Version)).ToArray();
    }

    /// <summary>从库里读取已提交的鉴权名单（只含有效令牌的哈希），不经过内存快照。</summary>
    public async Task<IReadOnlyList<ClientAccess>> ReadAccess() => await ReadCommitted(scopes);

    /// <summary>
    /// 新建使用者并签发第一版个人令牌：显示名去首尾空白后须为 1–64 个字符且不含控制字符；
    /// 未指定 <paramref name="legacyName"/> 时自动生成 <c>u_</c> 加 24 位随机小写十六进制的内部名称（与主键无关），旧客户端迁移时可沿用原名称但同样须合法。
    /// 人数上限与重名在写入的同一事务里判断。返回结果中的明文令牌只出现这一次，库里只存其哈希；使用者主键由库生成。
    /// </summary>
    public async Task<IssuedToken> Create(string displayName, string? legacyName = null)
    {
        displayName = ValidateDisplayName(displayName);
        var name = legacyName ?? "u_" + RandomNumberGenerator.GetHexString(24, lowercase: true);
        if (!RelayConfiguration.IsClientName(name)) throw new InvalidOperationException("客户端名称只能是小写字母、数字、短横线或下划线，最多 32 字符。");
        var token = ConfigurationFiles.NewToken();
        return await Change(async scope =>
        {
            var users = scope.Repository<User>(); var tokens = scope.Repository<Token>();
            User.EnsureRoomFor(await users.CountAsync());
            if (await users.AnyAsync(item => item.Name == name)) throw new InvalidOperationException("客户端名称已存在。");
            var user = new User { Name = name, DisplayName = displayName, Enabled = true };
            await users.AddAsync(user);
            // 先提交使用者拿到库生成的主键，令牌再按它关联；两次提交在同一个事务里，名单校验不过时一起回滚。
            await users.SaveChangesAsync();
            await tokens.AddAsync(new Token { UserId = user.Id, Version = 1, Hash = ConfigurationFiles.Hash(token) });
            await tokens.SaveChangesAsync();
            return new IssuedToken(user.Id, displayName, token);
        });
    }

    /// <summary>修改显示名（同样做长度与控制字符校验），令牌不变；使用者不存在时拒绝。</summary>
    public async Task Rename(long userId, string displayName)
    {
        displayName = ValidateDisplayName(displayName);
        await Change(async scope =>
        {
            var users = scope.Repository<User>();
            (await users.GetByIdAsync(userId) ?? throw MissingUser()).DisplayName = displayName;
            return await users.SaveChangesAsync();
        });
    }

    /// <summary>启用或停用使用者；提交后立即发布新名单，本进程的鉴权马上生效。使用者不存在时拒绝。</summary>
    public async Task SetEnabled(long userId, bool enabled) => await Change(async scope =>
    {
        var users = scope.Repository<User>();
        (await users.GetByIdAsync(userId) ?? throw MissingUser()).Enabled = enabled;
        return await users.SaveChangesAsync();
    });

    /// <summary>轮换个人令牌：撤销当前版、写入下一版的哈希，旧令牌随之失效；返回的明文新令牌只出现这一次。使用者不存在时拒绝。</summary>
    public async Task<IssuedToken> Rotate(long userId)
    {
        var token = ConfigurationFiles.NewToken();
        return await Change(async scope =>
        {
            var user = await scope.Repository<User>().GetByIdAsync(userId) ?? throw MissingUser();
            var tokens = scope.Repository<Token>();
            var owned = await tokens.FindAsync(item => item.UserId == userId);
            var now = DateTimeOffset.UtcNow;
            foreach (var current in owned.Where(item => item.RevokedAt is null)) current.Revoke(now);
            // 先提交撤销再写新版：库里「每名使用者只有一版未撤销」的唯一索引带条件，EF 排不出两条语句的先后，分两次提交（同一事务）最稳。
            await tokens.SaveChangesAsync();
            await tokens.AddAsync(new Token { UserId = userId, Version = owned.Select(item => item.Version).DefaultIfEmpty(0).Max() + 1, Hash = ConfigurationFiles.Hash(token) });
            await tokens.SaveChangesAsync();
            return new IssuedToken(userId, user.DisplayName, token);
        });
    }

    /// <summary>
    /// 读已提交的鉴权名单：每名使用者配上他当前未撤销的那一版令牌，按使用者主键排序。鉴权名单的依赖注入工厂也用它，名单只有这一种读法。
    /// </summary>
    /// <param name="scopes">作用域工厂。</param>
    public static async Task<IReadOnlyList<ClientAccess>> ReadCommitted(IServiceScopeFactory scopes)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await ReadAccess(scope);
    }

    /// <summary>在给定作用域里（写入时就是同一个事务里）读当前有效令牌的鉴权名单。</summary>
    private static async Task<IReadOnlyList<ClientAccess>> ReadAccess(AsyncServiceScope scope)
    {
        var users = (await scope.Repository<User>().GetAllAsync()).ToDictionary(item => item.Id);
        var tokens = await scope.Repository<Token>().FindAsync(item => item.RevokedAt == null);
        return tokens.Where(item => users.ContainsKey(item.UserId)).OrderBy(item => item.UserId)
            .Select(item => new ClientAccess(users[item.UserId].Name, item.Hash, users[item.UserId].Enabled, item.UserId, item.Id)).ToArray();
    }

    /// <summary>
    /// 串行执行一次写入：在一个事务里做改动、读回名单并校验，合格才提交，再用这份已提交名单替换鉴权快照，最后返回写入结果；
    /// 写入或名单校验失败时整个事务回滚、异常上抛，不发布新名单。
    /// </summary>
    private async Task<T> Change<T>(Func<AsyncServiceScope, Task<T>> change)
    {
        // 同一服务提交后立即发布已验证快照；失败时不发布，跨进程鉴权还会重新读库。
        await _writes.WaitAsync();
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var (value, access) = await scope.Repository<User>().ExecuteInTransactionAsync(async () =>
            {
                var value = await change(scope);
                var access = await ReadAccess(scope);
                RelayConfiguration.ValidateClients(access);
                return (value, access);
            });
            Clients.Replace(access);
            return value;
        }
        // 成功或失败都归还写入名额，否则之后的使用者管理会一直排队。
        finally { _writes.Release(); }
    }

    /// <summary>「使用者不存在」的读句。</summary>
    private static InvalidOperationException MissingUser() => new("使用者不存在。");

    /// <summary>去掉首尾空白后校验显示名为 1–64 个字符且不含控制字符，返回去空白后的值；不合法时抛出 <see cref="InvalidOperationException"/>。</summary>
    private static string ValidateDisplayName(string name)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 64 || name.Any(char.IsControl)) throw new InvalidOperationException("显示名应为 1 至 64 个字符，不包含控制字符。");
        return name;
    }
}
