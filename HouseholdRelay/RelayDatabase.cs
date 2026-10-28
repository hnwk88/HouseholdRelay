using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace HouseholdRelay;

/// <summary>本实例唯一的配置数据库；连接和事务只覆盖本地短操作。</summary>
public sealed class RelayDatabase
{
    public string Home { get; }
    public string State { get; }
    public string Path { get; }
    public string InstanceId { get; private set; } = "";
    private readonly string marker;
    private readonly string connectionString;

    public RelayDatabase(string home) : this(home, null) { }

    internal RelayDatabase(string home, Action? migrationCheckpoint)
    {
        Home = System.IO.Path.GetFullPath(home);
        State = System.IO.Path.Combine(Home, ".state");
        Path = System.IO.Path.Combine(State, "relay.db");
        marker = System.IO.Path.Combine(Home, ".relay-instance");
        connectionString = new SqliteConnectionStringBuilder
        { DataSource = Path, Mode = SqliteOpenMode.ReadWrite, ForeignKeys = true, DefaultTimeout = 10, Pooling = false }.ToString();
        // 只串行化建库和旧文件导入；普通网页和 CLI 写入由 SQLite 事务协调。
        using var startup = new Mutex(false, "Local\\HouseholdRelay-" + ConfigurationFiles.Hash(Home.ToUpperInvariant()));
        var acquired = false;
        try
        {
            try { acquired = startup.WaitOne(TimeSpan.FromSeconds(15)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new InvalidOperationException("另一进程正在初始化数据目录，请稍后重试。");
            WindowsSecrets.ProtectDirectory(State);
            if (File.Exists(marker) != File.Exists(Path))
                throw new InvalidOperationException("实例标记与数据库不完整。请恢复本实例备份，不能自动重新开放首次设置。");
            if (!File.Exists(marker)) Create();
            InstanceId = File.ReadAllText(marker).Trim();
            if (!Guid.TryParseExact(InstanceId, "N", out _)) throw new InvalidOperationException("实例标记损坏，请恢复备份。");
            using var connection = Open();
            if (Convert.ToInt32(Scalar(connection, null, "PRAGMA user_version")) is not (1 or 2)
                || (string?)Scalar(connection, null, "PRAGMA quick_check") != "ok")
                throw new InvalidOperationException("数据库版本不支持或完整性检查失败，请恢复备份。");
            LegacyImport.Run(this, migrationCheckpoint);
            UpstreamSchema.Upgrade(this);
        }
        finally
        {
            // 释放本次确实取得的互斥量，不处理其他服务进程。
            if (acquired) startup.ReleaseMutex();
        }
    }

    private void Create()
    {
        InstanceId = Guid.NewGuid().ToString("N");
        // 标记在数据库之外；写到一半即停机也不会把旧实例误认为未初始化。
        using (var file = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            file.Write(Encoding.ASCII.GetBytes(InstanceId));
        var createString = new SqliteConnectionStringBuilder(connectionString) { Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
        using var connection = new SqliteConnection(createString); connection.Open();
        Execute(connection, null, "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;");
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, """
            CREATE TABLE metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE settings (id INTEGER PRIMARY KEY CHECK(id=1), version INTEGER NOT NULL, json TEXT NOT NULL);
            CREATE TABLE administrators (id TEXT PRIMARY KEY, password_hash TEXT NOT NULL);
            CREATE UNIQUE INDEX one_administrator ON administrators((1));
            CREATE TABLE bootstrap (id INTEGER PRIMARY KEY CHECK(id=1), hash TEXT NOT NULL, protected BLOB NOT NULL);
            CREATE TABLE users (id TEXT PRIMARY KEY, name TEXT NOT NULL UNIQUE, display_name TEXT NOT NULL, enabled INTEGER NOT NULL CHECK(enabled IN (0,1)));
            CREATE TABLE tokens (id TEXT PRIMARY KEY, user_id TEXT NOT NULL REFERENCES users(id), version INTEGER NOT NULL,
                hash TEXT NOT NULL UNIQUE, revoked_at TEXT, UNIQUE(user_id,version));
            CREATE UNIQUE INDEX one_active_token ON tokens(user_id) WHERE revoked_at IS NULL;
            CREATE TABLE upstream (id INTEGER PRIMARY KEY CHECK(id=1), version INTEGER NOT NULL, mode TEXT NOT NULL,
                base_url TEXT NOT NULL, credential BLOB);
            CREATE TABLE secrets (name TEXT PRIMARY KEY, protected BLOB NOT NULL);
            CREATE TABLE public_assets (name TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE admin_sessions (id TEXT PRIMARY KEY, admin_id TEXT NOT NULL REFERENCES administrators(id), expires_at INTEGER NOT NULL);
            INSERT INTO upstream VALUES(1,1,'oauth','https://api.anthropic.com',NULL);
            PRAGMA user_version=1;
            """);
        Execute(connection, transaction, "INSERT INTO metadata VALUES('instance_id',$id),('legacy_imported','0')", ("$id", InstanceId));
        Execute(connection, transaction, "INSERT INTO settings VALUES(1,1,$json)", ("$json", SerializeSettings(new RelayConfiguration())));
        var code = ConfigurationFiles.NewToken();
        Execute(connection, transaction, "INSERT INTO bootstrap VALUES(1,$hash,$protected)",
            ("$hash", ConfigurationFiles.Hash(code)), ("$protected", WindowsSecrets.Protect(Encoding.UTF8.GetBytes(code))));
        transaction.Commit();
    }

    /// <summary>已有数据库只以读写模式打开；文件遗失时绝不悄悄建空库。</summary>
    public SqliteConnection Open()
    {
        if (!File.Exists(Path) || !File.Exists(marker) || File.ReadAllText(marker).Trim() != InstanceId)
            throw new InvalidOperationException("实例数据不可用，请检查数据目录或恢复备份。");
        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();
            Execute(connection, null, "PRAGMA synchronous=FULL;");
            if ((string?)Scalar(connection, null, "SELECT value FROM metadata WHERE key='instance_id'") != InstanceId)
                throw new InvalidOperationException("数据库不属于当前实例，请恢复配套备份。");
            return connection;
        }
        catch
        {
            // 未交付的连接由创建方释放，调用者不会获得半初始化连接。
            connection.Dispose(); throw;
        }
    }

    public T Write<T>(Func<SqliteConnection, SqliteTransaction, T> action)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var result = action(connection, transaction);
        transaction.Commit();
        return result;
    }

    public static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
    public static int Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    { using var command = Command(connection, transaction, sql, parameters); return command.ExecuteNonQuery(); }
    public static object? Scalar(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    { using var command = Command(connection, transaction, sql, parameters); var value = command.ExecuteScalar(); return value is DBNull ? null : value; }

    internal static string SerializeSettings(RelayConfiguration config)
    {
        var copy = JsonSerializer.Deserialize<RelayConfiguration>(JsonSerializer.Serialize(config, ConfigurationFiles.Json), ConfigurationFiles.Json)!;
        copy.Clients = [];
        return JsonSerializer.Serialize(copy, ConfigurationFiles.Json);
    }
}
