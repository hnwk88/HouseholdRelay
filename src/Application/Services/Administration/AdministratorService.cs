using System.Security.Cryptography;
using System.Text;
using Application.DependencyInjection;
using Application.Interfaces.Administration;
using Application.Interfaces.Security;
using Core.Configuration;
using Core.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Application.Services.Administration;

/// <summary>本机一次性引导和可撤销管理会话，与个人中转令牌完全分离。数据经通用仓储存取，每次操作一个作用域。</summary>
public sealed class AdministratorService(IServiceScopeFactory scopes, IAdministratorPasswordHasher hasher, ISecretProtector protector) : IAdministratorService
{
    /// <summary>是否已完成首次设置（恰好存在一名管理员）；每次都直接查库，不做缓存。</summary>
    public async Task<bool> IsInitialized()
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.Repository<Administrator>().CountAsync() == 1;
    }

    /// <summary>解密并返回一次性引导码，供本机启动时显示；首次设置完成后库里已无引导码，返回 null。解密用的明文缓冲在返回前清零。</summary>
    public async Task<string?> BootstrapCode()
    {
        await using var scope = scopes.CreateAsyncScope();
        if (await scope.Repository<BootstrapCode>().GetByIdAsync(Core.Identity.BootstrapCode.SingletonId) is not { } code) return null;
        var bytes = protector.Unprotect(code.Protected);
        try { return Encoding.UTF8.GetString(bytes); }
        // 无论解码是否成功都清零解密出的明文字节，缩短引导码在内存中的停留时间。
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    /// <summary>
    /// 用引导码完成首次设置：密码须为 12–128 个字符，引导码超过 512 字符直接判为不正确；
    /// 在一个事务里核对还没有管理员、引导码哈希相符，写入管理员（主键由库生成）并物理删除引导码。不合法时抛出 <see cref="InvalidOperationException"/>。
    /// </summary>
    public async Task Initialize(string code, string password)
    {
        if (password.Length is < 12 or > 128) throw new InvalidOperationException("管理密码需要 12 至 128 个字符。");
        if (code.Length > 512) throw new InvalidOperationException("引导码不正确。");
        var passwordHash = hasher.Hash(password); var codeHash = ConfigurationFiles.Hash(code);
        await using var scope = scopes.CreateAsyncScope();
        var administrators = scope.Repository<Administrator>(); var bootstrap = scope.Repository<BootstrapCode>();
        await administrators.ExecuteInTransactionAsync(async () =>
        {
            if (await administrators.CountAsync() != 0) throw new InvalidOperationException("首次设置已经完成，请登录。");
            var stored = await bootstrap.GetByIdAsync(Core.Identity.BootstrapCode.SingletonId);
            if (stored is null || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(stored.Hash), Convert.FromHexString(codeHash)))
                throw new InvalidOperationException("引导码不正确，请查看本机启动窗口。");
            await administrators.AddAsync(new Administrator { PasswordHash = passwordHash });
            // 引导码用过即作废，从库里真删掉，不留软删除的密文。
            await bootstrap.DeleteAsync(stored, isHardDelete: true);
            return await administrators.SaveChangesAsync();
        });
    }

    /// <summary>
    /// 校验管理密码，通过后签发 8 小时有效的会话令牌（库里只存其哈希）并返回明文令牌；同一次提交里物理删除已过期的会话。
    /// 密码长度不在 1–128、尚未设置或密码错误时返回 null。
    /// </summary>
    public async Task<string?> Login(string password)
    {
        if (password.Length is < 1 or > 128) return null;
        await using var scope = scopes.CreateAsyncScope();
        var administrators = scope.Repository<Administrator>();
        if ((await administrators.GetAllAsync()).FirstOrDefault() is not { } administrator) return null;
        if (!hasher.Verify(administrator.PasswordHash, password)) return null;
        var session = ConfigurationFiles.NewToken(); var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sessions = scope.Repository<AdministratorSession>();
        await sessions.DeleteRangeAsync(await sessions.FindAsync(item => item.ExpiresAt <= now), isHardDelete: true);
        await sessions.AddAsync(new AdministratorSession { TokenHash = ConfigurationFiles.Hash(session), AdministratorId = administrator.Id, ExpiresAt = now + 8 * 3600 });
        await sessions.SaveChangesAsync();
        return session;
    }

    /// <summary>按当前 UTC 时间判断会话是否存在且未过期；空值或超过 512 字符的会话直接视为无效，不查库。</summary>
    public async Task<bool> IsSessionValid(string? session)
    {
        if (string.IsNullOrEmpty(session) || session.Length > 512) return false;
        var hash = ConfigurationFiles.Hash(session); var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await using var scope = scopes.CreateAsyncScope();
        return await scope.Repository<AdministratorSession>().AnyAsync(item => item.TokenHash == hash && item.ExpiresAt > now);
    }

    /// <summary>物理删除会话使其立即失效，复制出去的 Cookie 随之作废；会话为空时什么也不做。</summary>
    public async Task Logout(string? session)
    {
        if (string.IsNullOrEmpty(session)) return;
        var hash = ConfigurationFiles.Hash(session);
        await using var scope = scopes.CreateAsyncScope();
        var sessions = scope.Repository<AdministratorSession>();
        await sessions.DeleteRangeAsync(await sessions.FindAsync(item => item.TokenHash == hash), isHardDelete: true);
        await sessions.SaveChangesAsync();
    }
}
