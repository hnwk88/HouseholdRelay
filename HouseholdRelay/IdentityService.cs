using Microsoft.Data.Sqlite;
using static HouseholdRelay.RelayDatabase;

namespace HouseholdRelay;

public sealed record RelayUser(string Id, string Name, string DisplayName, bool Enabled, int TokenVersion);
public sealed record IssuedToken(string UserId, string DisplayName, string Token);

/// <summary>网页和 CLI 共用的使用者管理；明文令牌不进入持久化模型。</summary>
public sealed class IdentityService
{
    private readonly RelayDatabase database;
    private readonly object writes = new();
    public ClientRegistry Clients { get; }
    public IdentityService(RelayDatabase database)
    {
        this.database = database;
        Clients = new ClientRegistry(ReadAccess(), ReadAccess);
    }

    public IReadOnlyList<RelayUser> List()
    {
        using var connection = database.Open();
        using var command = Command(connection, null, """
            SELECT u.id,u.name,u.display_name,u.enabled,t.version FROM users u
            JOIN tokens t ON t.user_id=u.id AND t.revoked_at IS NULL ORDER BY u.rowid
            """);
        using var reader = command.ExecuteReader(); var users = new List<RelayUser>();
        while (reader.Read()) users.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3), reader.GetInt32(4)));
        return users;
    }

    public IssuedToken Create(string displayName, string? legacyName = null)
    {
        displayName = ValidateDisplayName(displayName);
        var id = Guid.NewGuid().ToString("N"); var name = legacyName ?? "u_" + id[..24];
        if (!RelayConfiguration.IsClientName(name)) throw new InvalidOperationException("客户端名称只能是小写字母、数字、短横线或下划线，最多 32 字符。");
        var token = ConfigurationFiles.NewToken();
        return Change((connection, transaction) =>
        {
            if (Convert.ToInt32(Scalar(connection, transaction, "SELECT COUNT(*) FROM users")) >= 8)
                throw new InvalidOperationException("最多支持 8 名使用者；轮换令牌不会占用新的名额。");
            if (Scalar(connection, transaction, "SELECT id FROM users WHERE name=$name", ("$name", name)) is not null)
                throw new InvalidOperationException("客户端名称已存在。");
            Execute(connection, transaction, "INSERT INTO users VALUES($id,$name,$display,1)", ("$id", id), ("$name", name), ("$display", displayName));
            Execute(connection, transaction, "INSERT INTO tokens VALUES($id,$user,1,$hash,NULL)",
                ("$id", Guid.NewGuid().ToString("N")), ("$user", id), ("$hash", ConfigurationFiles.Hash(token)));
            return new IssuedToken(id, displayName, token);
        });
    }

    public void Rename(string userId, string displayName)
    {
        displayName = ValidateDisplayName(displayName);
        Change((connection, transaction) => RequireUser(Execute(connection, transaction,
            "UPDATE users SET display_name=$display WHERE id=$id", ("$display", displayName), ("$id", userId))));
    }
    public void SetEnabled(string userId, bool enabled) => Change((connection, transaction) => RequireUser(Execute(connection, transaction,
        "UPDATE users SET enabled=$enabled WHERE id=$id", ("$enabled", enabled), ("$id", userId))));

    public IssuedToken Rotate(string userId)
    {
        var token = ConfigurationFiles.NewToken();
        return Change((connection, transaction) =>
        {
            var name = Scalar(connection, transaction, "SELECT display_name FROM users WHERE id=$id", ("$id", userId)) as string
                ?? throw new InvalidOperationException("使用者不存在。");
            var version = Convert.ToInt32(Scalar(connection, transaction, "SELECT MAX(version) FROM tokens WHERE user_id=$id", ("$id", userId))) + 1;
            Execute(connection, transaction, "UPDATE tokens SET revoked_at=$time WHERE user_id=$id AND revoked_at IS NULL",
                ("$time", DateTimeOffset.UtcNow.ToString("O")), ("$id", userId));
            Execute(connection, transaction, "INSERT INTO tokens VALUES($id,$user,$version,$hash,NULL)",
                ("$id", Guid.NewGuid().ToString("N")), ("$user", userId), ("$version", version), ("$hash", ConfigurationFiles.Hash(token)));
            return new IssuedToken(userId, name, token);
        });
    }

    private T Change<T>(Func<SqliteConnection, SqliteTransaction, T> change)
    {
        // 同一服务提交后立即发布已验证快照；失败时不发布，跨进程鉴权还会重新读库。
        lock (writes)
        {
            var result = database.Write((connection, transaction) =>
            {
                var value = change(connection, transaction);
                var permissions = ReadAccess(connection, transaction);
                RelayConfiguration.ValidateClients(permissions);
                return (Value: value, Permissions: permissions);
            });
            Clients.Replace(result.Permissions);
            return result.Value;
        }
    }
    public IReadOnlyList<ClientAccess> ReadAccess()
    { using var connection = database.Open(); return ReadAccess(connection, null); }
    private static IReadOnlyList<ClientAccess> ReadAccess(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = Command(connection, transaction, """
            SELECT u.name,t.hash,u.enabled,u.id,t.id FROM users u JOIN tokens t ON t.user_id=u.id AND t.revoked_at IS NULL
            """);
        using var reader = command.ExecuteReader(); var clients = new List<ClientAccess>();
        while (reader.Read()) clients.Add(new(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2), reader.GetString(3), reader.GetString(4)));
        return clients;
    }
    private static int RequireUser(int count) => count == 1 ? count : throw new InvalidOperationException("使用者不存在。");
    private static string ValidateDisplayName(string name)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 64 || name.Any(char.IsControl)) throw new InvalidOperationException("显示名应为 1 至 64 个字符，不包含控制字符。");
        return name;
    }
}
