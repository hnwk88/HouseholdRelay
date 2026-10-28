using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using static HouseholdRelay.RelayDatabase;

namespace HouseholdRelay;

/// <summary>本机一次性引导和可撤销管理会话，与个人中转令牌完全分离。</summary>
public sealed class AdministratorService(RelayDatabase database)
{
    private static readonly PasswordHasher<string> Hasher = new(Options.Create(new PasswordHasherOptions { IterationCount = 210_000 }));
    public bool IsInitialized
    {
        get { using var connection = database.Open(); return Convert.ToInt32(Scalar(connection, null, "SELECT COUNT(*) FROM administrators")) == 1; }
    }
    public string? BootstrapCode()
    {
        using var connection = database.Open();
        if (Scalar(connection, null, "SELECT protected FROM bootstrap WHERE id=1") is not byte[] encrypted) return null;
        var bytes = WindowsSecrets.Unprotect(encrypted);
        try { return Encoding.UTF8.GetString(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public void Initialize(string code, string password)
    {
        if (password.Length is < 12 or > 128) throw new InvalidOperationException("管理密码需要 12 至 128 个字符。");
        if (code.Length > 512) throw new InvalidOperationException("引导码不正确。");
        var id = Guid.NewGuid().ToString("N");
        var passwordHash = Hasher.HashPassword(id, password);
        database.Write((connection, transaction) =>
        {
            if (Convert.ToInt32(Scalar(connection, transaction, "SELECT COUNT(*) FROM administrators")) != 0)
                throw new InvalidOperationException("首次设置已经完成，请登录。");
            var hash = Scalar(connection, transaction, "SELECT hash FROM bootstrap WHERE id=1") as string;
            if (hash is null || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(hash), Convert.FromHexString(ConfigurationFiles.Hash(code))))
                throw new InvalidOperationException("引导码不正确，请查看本机启动窗口。");
            Execute(connection, transaction, "INSERT INTO administrators VALUES($id,$hash)", ("$id", id), ("$hash", passwordHash));
            return Execute(connection, transaction, "DELETE FROM bootstrap");
        });
    }

    public string? Login(string password)
    {
        if (password.Length is < 1 or > 128) return null;
        string id, hash;
        using (var connection = database.Open())
        using (var command = Command(connection, null, "SELECT id,password_hash FROM administrators LIMIT 1"))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) return null;
            id = reader.GetString(0); hash = reader.GetString(1);
        }
        if (Hasher.VerifyHashedPassword(id, hash, password) == PasswordVerificationResult.Failed) return null;
        var session = ConfigurationFiles.NewToken(); var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        database.Write((connection, transaction) =>
        {
            Execute(connection, transaction, "DELETE FROM admin_sessions WHERE expires_at<=$now", ("$now", now));
            return Execute(connection, transaction, "INSERT INTO admin_sessions VALUES($id,$admin,$expires)",
                ("$id", ConfigurationFiles.Hash(session)), ("$admin", id), ("$expires", now + 8 * 3600));
        });
        return session;
    }
    public bool IsSessionValid(string? session)
    {
        if (string.IsNullOrEmpty(session) || session.Length > 512) return false;
        using var connection = database.Open();
        return Scalar(connection, null, "SELECT id FROM admin_sessions WHERE id=$id AND expires_at>$now",
            ("$id", ConfigurationFiles.Hash(session)), ("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds())) is not null;
    }
    public void Logout(string? session)
    {
        if (string.IsNullOrEmpty(session)) return;
        database.Write((connection, transaction) => Execute(connection, transaction, "DELETE FROM admin_sessions WHERE id=$id", ("$id", ConfigurationFiles.Hash(session))));
    }
}
