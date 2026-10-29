using System.Security.Cryptography;
using System.Text;
using Application.Interfaces.Administration;
using Application.Interfaces.Security;
using Core.Configuration;

namespace Application.Services.Administration;

/// <summary>本机一次性引导和可撤销管理会话，与个人中转令牌完全分离。</summary>
public sealed class AdministratorService(IAdministratorRepository repository, IAdministratorPasswordHasher hasher, ISecretProtector protector) : IAdministratorService
{
    /// <summary>是否已完成首次设置；每次都直接查询仓储，不做缓存。</summary>
    public bool IsInitialized => repository.IsInitialized();

    /// <summary>解密并返回一次性引导码，供本机启动时显示；首次设置完成后仓储里已无引导码，返回 null。解密用的明文缓冲在返回前清零。</summary>
    public string? BootstrapCode()
    {
        if (repository.ReadProtectedBootstrapCode() is not byte[] encrypted) return null;
        var bytes = protector.Unprotect(encrypted);
        try { return Encoding.UTF8.GetString(bytes); }
        // 无论解码是否成功都清零解密出的明文字节，缩短引导码在内存中的停留时间。
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    /// <summary>
    /// 用引导码完成首次设置：密码须为 12–128 个字符，引导码超过 512 字符直接判为不正确；
    /// 生成新的管理员主键并哈希密码后交给仓储，由仓储在事务内核对引导码哈希、写入管理员并删除引导码。不合法时抛出 <see cref="InvalidOperationException"/>。
    /// </summary>
    public void Initialize(string code, string password)
    {
        if (password.Length is < 12 or > 128) throw new InvalidOperationException("管理密码需要 12 至 128 个字符。");
        if (code.Length > 512) throw new InvalidOperationException("引导码不正确。");
        var id = Guid.NewGuid().ToString("N");
        var passwordHash = hasher.Hash(id, password);
        repository.Initialize(id, passwordHash, ConfigurationFiles.Hash(code));
    }

    /// <summary>校验管理密码，通过后签发 8 小时有效的会话令牌（库里只存其哈希）并返回明文令牌；密码长度不在 1–128、尚未设置或密码错误时返回 null。</summary>
    public string? Login(string password)
    {
        if (password.Length is < 1 or > 128) return null;
        if (repository.ReadAdministrator() is not { } administrator) return null;
        if (!hasher.Verify(administrator.Id, administrator.PasswordHash, password)) return null;
        var session = ConfigurationFiles.NewToken(); var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        repository.CreateSession(ConfigurationFiles.Hash(session), administrator.Id, now, now + 8 * 3600);
        return session;
    }
    /// <summary>按当前 UTC 时间判断会话是否存在且未过期；空值或超过 512 字符的会话直接视为无效，不查库。</summary>
    public bool IsSessionValid(string? session)
    {
        if (string.IsNullOrEmpty(session) || session.Length > 512) return false;
        return repository.IsSessionValid(ConfigurationFiles.Hash(session), DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    /// <summary>删除会话使其立即失效；会话为空时什么也不做。</summary>
    public void Logout(string? session)
    {
        if (string.IsNullOrEmpty(session)) return;
        repository.DeleteSession(ConfigurationFiles.Hash(session));
    }
}
