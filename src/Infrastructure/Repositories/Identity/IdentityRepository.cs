using Application.Contracts.Identity;
using Application.Interfaces.Identity;
using Core.Configuration;
using Core.Identity;
using Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using static Infrastructure.Persistence.RelayDatabase;

namespace Infrastructure.Repositories.Identity;

/// <summary>users 与 tokens 表的读写；每次写入在同一事务里读回并校验鉴权名单，校验失败整体回滚。</summary>
internal sealed class IdentityRepository(RelayDatabase database) : IIdentityRepository
{
    /// <summary>按创建顺序列出使用者。</summary>
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

    /// <summary>读取已提交的鉴权名单。</summary>
    public IReadOnlyList<ClientAccess> ReadAccess()
    { using var connection = database.Open(); return ReadAccess(connection, null); }

    /// <summary>新建使用者与第一版令牌。</summary>
    public IReadOnlyList<ClientAccess> Create(string userId, string name, string displayName, string tokenHash) => Change((connection, transaction) =>
    {
        if (Convert.ToInt32(Scalar(connection, transaction, "SELECT COUNT(*) FROM users")) >= 8)
            throw new InvalidOperationException("最多支持 8 名使用者；轮换令牌不会占用新的名额。");
        if (Scalar(connection, transaction, "SELECT id FROM users WHERE name=$name", ("$name", name)) is not null)
            throw new InvalidOperationException("客户端名称已存在。");
        Execute(connection, transaction, "INSERT INTO users VALUES($id,$name,$display,1)", ("$id", userId), ("$name", name), ("$display", displayName));
        Execute(connection, transaction, "INSERT INTO tokens VALUES($id,$user,1,$hash,NULL)",
            ("$id", Guid.NewGuid().ToString("N")), ("$user", userId), ("$hash", tokenHash));
        return 0;
    }).Permissions;

    /// <summary>修改显示名。</summary>
    public IReadOnlyList<ClientAccess> Rename(string userId, string displayName) => Change((connection, transaction) => RequireUser(Execute(connection, transaction,
        "UPDATE users SET display_name=$display WHERE id=$id", ("$display", displayName), ("$id", userId)))).Permissions;

    /// <summary>启用或停用使用者。</summary>
    public IReadOnlyList<ClientAccess> SetEnabled(string userId, bool enabled) => Change((connection, transaction) => RequireUser(Execute(connection, transaction,
        "UPDATE users SET enabled=$enabled WHERE id=$id", ("$enabled", enabled), ("$id", userId)))).Permissions;

    /// <summary>撤销当前令牌并写入下一版。</summary>
    public RotatedIdentity Rotate(string userId, string tokenHash)
    {
        var result = Change((connection, transaction) =>
        {
            var name = Scalar(connection, transaction, "SELECT display_name FROM users WHERE id=$id", ("$id", userId)) as string
                ?? throw new InvalidOperationException("使用者不存在。");
            var version = Convert.ToInt32(Scalar(connection, transaction, "SELECT MAX(version) FROM tokens WHERE user_id=$id", ("$id", userId))) + 1;
            Execute(connection, transaction, "UPDATE tokens SET revoked_at=$time WHERE user_id=$id AND revoked_at IS NULL",
                ("$time", DateTimeOffset.UtcNow.ToString("O")), ("$id", userId));
            Execute(connection, transaction, "INSERT INTO tokens VALUES($id,$user,$version,$hash,NULL)",
                ("$id", Guid.NewGuid().ToString("N")), ("$user", userId), ("$version", version), ("$hash", tokenHash));
            return name;
        });
        return new RotatedIdentity(result.Value, result.Permissions);
    }

    /// <summary>在一个写事务里执行变更、读回并校验鉴权名单，返回变更结果与提交后的名单。</summary>
    private (T Value, IReadOnlyList<ClientAccess> Permissions) Change<T>(Func<SqliteConnection, SqliteTransaction, T> change) => database.Write((connection, transaction) =>
    {
        // 名单校验在提交前完成；不合法时整个事务回滚，调用方不会发布部分撤销。
        var value = change(connection, transaction);
        var permissions = ReadAccess(connection, transaction);
        RelayConfiguration.ValidateClients(permissions);
        return (value, permissions);
    });
    /// <summary>在给定连接与事务中读取当前有效令牌的鉴权名单。</summary>
    private static IReadOnlyList<ClientAccess> ReadAccess(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = Command(connection, transaction, """
            SELECT u.name,t.hash,u.enabled,u.id,t.id FROM users u JOIN tokens t ON t.user_id=u.id AND t.revoked_at IS NULL
            """);
        using var reader = command.ExecuteReader(); var clients = new List<ClientAccess>();
        while (reader.Read()) clients.Add(new(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2), reader.GetString(3), reader.GetString(4)));
        return clients;
    }
    /// <summary>更新行数不是 1 时说明使用者不存在。</summary>
    private static int RequireUser(int count) => count == 1 ? count : throw new InvalidOperationException("使用者不存在。");
}
