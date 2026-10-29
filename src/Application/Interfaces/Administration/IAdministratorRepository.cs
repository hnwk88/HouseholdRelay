using Application.Contracts.Administration;

namespace Application.Interfaces.Administration;

/// <summary>管理员、一次性引导码与管理会话的持久化。</summary>
public interface IAdministratorRepository
{
    /// <summary>是否已完成首次设置（恰好存在一名管理员）。</summary>
    bool IsInitialized();

    /// <summary>读取加密保存的一次性引导码；首次设置完成后为空。</summary>
    byte[]? ReadProtectedBootstrapCode();

    /// <summary>在一个事务里核对引导码哈希、写入管理员并删除引导码；已设置或引导码不符时拒绝。</summary>
    void Initialize(string administratorId, string passwordHash, string bootstrapCodeHash);

    /// <summary>读取唯一管理员的主键与密码哈希；尚未设置时为空。</summary>
    AdministratorCredential? ReadAdministrator();

    /// <summary>清理已过期会话并写入新会话（只保存会话令牌的哈希）。</summary>
    void CreateSession(string sessionHash, string administratorId, long now, long expiresAt);

    /// <summary>会话哈希是否存在且未过期。</summary>
    bool IsSessionValid(string sessionHash, long now);

    /// <summary>删除会话，复制出去的 Cookie 随之失效。</summary>
    void DeleteSession(string sessionHash);
}
