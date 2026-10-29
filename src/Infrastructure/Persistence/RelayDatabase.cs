using System.Text;
using System.Text.Json;
using Application.Interfaces.Configuration;
using Application.Interfaces.Security;
using Core.Configuration;
using Microsoft.Data.Sqlite;

namespace Infrastructure.Persistence;

/// <summary>本实例唯一的配置数据库；连接和事务只覆盖本地短操作。</summary>
public sealed class RelayDatabase : IRelayInstance
{
    /// <summary>数据目录的完整路径（来自 --home，默认程序目录），实例标记文件放在它下面。</summary>
    public string Home { get; }
    /// <summary>数据目录下的 .state 子目录，启动时以 DPAPI 目录保护限制访问，存放数据库、旧数据备份等受保护状态。</summary>
    public string State { get; }
    /// <summary>SQLite 数据库文件 relay.db 的完整路径。</summary>
    public string Path { get; }
    /// <summary>本实例的唯一编号（32 位十六进制 GUID），同时写在库外的 .relay-instance 标记和库内元数据中，每次打开连接都核对二者一致，防止误用别的实例的数据库或备份。</summary>
    public string InstanceId { get; private set; } = "";
    private readonly string marker;
    private readonly string connectionString;
    private readonly ISecretProtector protector;

    /// <summary>打开或首次创建实例数据库：用按数据目录命名的跨进程互斥量（最多等 15 秒）串行化初始化；实例标记与数据库必须同时存在或同时缺失，否则拒绝启动而不是重开首次设置；随后检查库版本与完整性，再按顺序执行全部迁移。作为单例由依赖注入创建，任何不一致都抛异常阻止启动。</summary>
    public RelayDatabase(RelayDataOptions options, ISecretProtector protector, IEnumerable<IDatabaseMigration> migrations)
    {
        this.protector = protector;
        Home = System.IO.Path.GetFullPath(options.Home);
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
            // 上一个持有者异常退出时互斥量会被遗弃，此时已归当前线程所有；后续迁移各自带事务且可重入，可以继续初始化。
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new InvalidOperationException("另一进程正在初始化数据目录，请稍后重试。");
            protector.ProtectDirectory(State);
            if (File.Exists(marker) != File.Exists(Path))
                throw new InvalidOperationException("实例标记与数据库不完整。请恢复本实例备份，不能自动重新开放首次设置。");
            if (!File.Exists(marker)) Create();
            InstanceId = File.ReadAllText(marker).Trim();
            if (!Guid.TryParseExact(InstanceId, "N", out _)) throw new InvalidOperationException("实例标记损坏，请恢复备份。");
            using (var connection = Open())
            {
                if (Convert.ToInt32(Scalar(connection, null, "PRAGMA user_version")) is not (1 or 2 or 3)
                    || (string?)Scalar(connection, null, "PRAGMA quick_check") != "ok")
                    throw new InvalidOperationException("数据库版本不支持或完整性检查失败，请恢复备份。");
            }
            // 顺序固定为：旧数据导入、上游架构升级、用量架构升级。
            foreach (var migration in migrations.OrderBy(item => item.Order)) migration.Apply(this);
        }
        finally
        {
            // 释放本次确实取得的互斥量，不处理其他服务进程。
            if (acquired) startup.ReleaseMutex();
        }
    }

    /// <summary>首次初始化：先以直写方式新建实例标记，再新建数据库并开启 WAL 与完全同步，在一个事务内建立全部基础表、默认设置和上游行，并生成一次性首次设置码（库中存哈希和 DPAPI 密文）。只在互斥量保护下由构造函数调用。</summary>
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
            ("$hash", ConfigurationFiles.Hash(code)), ("$protected", protector.Protect(Encoding.UTF8.GetBytes(code))));
        transaction.Commit();
    }

    /// <summary>已有数据库只以读写模式打开；文件遗失时绝不悄悄建空库。</summary>
    public SqliteConnection Open(int? timeoutSeconds = null)
    {
        if (!File.Exists(Path) || !File.Exists(marker) || File.ReadAllText(marker).Trim() != InstanceId)
            throw new InvalidOperationException("实例数据不可用，请检查数据目录或恢复备份。");
        var connection = new SqliteConnection(timeoutSeconds is null ? connectionString
            : new SqliteConnectionStringBuilder(connectionString) { DefaultTimeout = timeoutSeconds.Value }.ToString());
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

    /// <summary>打开一个新连接并开启事务执行写操作，委托正常返回才提交；委托抛异常时事务随释放回滚。连接和事务只在本次调用内存在，适合本地短操作。</summary>
    public T Write<T>(Func<SqliteConnection, SqliteTransaction, T> action)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var result = action(connection, transaction);
        transaction.Commit();
        return result;
    }

    /// <summary>创建绑定到给定连接和事务的参数化命令，null 参数值转为数据库 NULL；所有 SQL 都经这里传参，避免拼接。返回的命令由调用方释放。</summary>
    public static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
    /// <summary>执行一条不返回结果集的参数化 SQL，返回受影响行数；命令在方法内释放。</summary>
    public static int Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    { using var command = Command(connection, transaction, sql, parameters); return command.ExecuteNonQuery(); }
    /// <summary>执行参数化查询并返回第一行第一列，数据库 NULL 或无结果都返回 null；命令在方法内释放。</summary>
    public static object? Scalar(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    { using var command = Command(connection, transaction, sql, parameters); var value = command.ExecuteScalar(); return value is DBNull ? null : value; }

    /// <summary>把配置序列化为 settings 表的 JSON：先深拷贝再清空客户端列表，因为用户和令牌哈希单独存表，不应重复写进设置。</summary>
    internal static string SerializeSettings(RelayConfiguration config)
    {
        var copy = JsonSerializer.Deserialize<RelayConfiguration>(JsonSerializer.Serialize(config, ConfigurationFiles.Json), ConfigurationFiles.Json)!;
        copy.Clients = [];
        return JsonSerializer.Serialize(copy, ConfigurationFiles.Json);
    }
}
