using System.Security.Cryptography;
using Application.Contracts.Administration;
using Application.Interfaces.Administration;
using Infrastructure.Persistence;
using static Infrastructure.Persistence.RelayDatabase;

namespace Infrastructure.Repositories.Administration;

/// <summary>administrators、bootstrap 与 admin_sessions 表的读写。</summary>
internal sealed class AdministratorRepository(RelayDatabase database) : IAdministratorRepository
{
    /// <summary>是否恰好存在一名管理员。</summary>
    public bool IsInitialized()
    { using var connection = database.Open(); return Convert.ToInt32(Scalar(connection, null, "SELECT COUNT(*) FROM administrators")) == 1; }

    /// <summary>读取加密的一次性引导码。</summary>
    public byte[]? ReadProtectedBootstrapCode()
    {
        using var connection = database.Open();
        return Scalar(connection, null, "SELECT protected FROM bootstrap WHERE id=1") as byte[];
    }

    /// <summary>核对引导码哈希、写入管理员并删除引导码。</summary>
    public void Initialize(string administratorId, string passwordHash, string bootstrapCodeHash) => database.Write((connection, transaction) =>
    {
        if (Convert.ToInt32(Scalar(connection, transaction, "SELECT COUNT(*) FROM administrators")) != 0)
            throw new InvalidOperationException("首次设置已经完成，请登录。");
        var hash = Scalar(connection, transaction, "SELECT hash FROM bootstrap WHERE id=1") as string;
        if (hash is null || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(hash), Convert.FromHexString(bootstrapCodeHash)))
            throw new InvalidOperationException("引导码不正确，请查看本机启动窗口。");
        Execute(connection, transaction, "INSERT INTO administrators VALUES($id,$hash)", ("$id", administratorId), ("$hash", passwordHash));
        return Execute(connection, transaction, "DELETE FROM bootstrap");
    });

    /// <summary>读取唯一管理员。</summary>
    public AdministratorCredential? ReadAdministrator()
    {
        using var connection = database.Open();
        using var command = Command(connection, null, "SELECT id,password_hash FROM administrators LIMIT 1");
        using var reader = command.ExecuteReader();
        return reader.Read() ? new AdministratorCredential(reader.GetString(0), reader.GetString(1)) : null;
    }

    /// <summary>清理过期会话并写入新会话。</summary>
    public void CreateSession(string sessionHash, string administratorId, long now, long expiresAt) => database.Write((connection, transaction) =>
    {
        Execute(connection, transaction, "DELETE FROM admin_sessions WHERE expires_at<=$now", ("$now", now));
        return Execute(connection, transaction, "INSERT INTO admin_sessions VALUES($id,$admin,$expires)",
            ("$id", sessionHash), ("$admin", administratorId), ("$expires", expiresAt));
    });

    /// <summary>会话是否存在且未过期。</summary>
    public bool IsSessionValid(string sessionHash, long now)
    {
        using var connection = database.Open();
        return Scalar(connection, null, "SELECT id FROM admin_sessions WHERE id=$id AND expires_at>$now", ("$id", sessionHash), ("$now", now)) is not null;
    }

    /// <summary>删除会话。</summary>
    public void DeleteSession(string sessionHash) =>
        database.Write((connection, transaction) => Execute(connection, transaction, "DELETE FROM admin_sessions WHERE id=$id", ("$id", sessionHash)));
}
