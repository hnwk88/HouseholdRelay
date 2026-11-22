using System.Text;
using System.Text.Json;
using Application.Interfaces.Configuration;
using Application.Interfaces.Security;
using Core.Configuration;
using Microsoft.Data.Sqlite;

namespace Infrastructure.Persistence;

/// <summary>
/// 本实例唯一的配置数据库：建库、按顺序跑迁移链、升级前在线备份，并给出连接串与每次打开连接都要做的两项核对。
/// 运行时的读写一律经 EF Core 与通用仓储（<see cref="RelayDbContext"/>），本类不再提供运行时读写入口；<see cref="Command"/> 等只给迁移链用。
/// </summary>
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
    /// <summary>本程序认识的最高库版本；更高的库（新版程序写过的）拒绝打开，加一版只改这里和新迁移类。</summary>
    public const int CurrentVersion = 11;
    /// <summary>
    /// 运行时与迁移链共用的连接串：只读写已有文件（文件不在绝不悄悄建空库）、开外键检查、遇锁最多等 10 秒、不用连接池（每次打开都重新核对实例）。
    /// </summary>
    internal string ConnectionString => _connectionString;
    /// <summary>最近一次架构升级的结果，存在 metadata 的 last_upgrade 里；从没升级过时为 null。</summary>
    public DatabaseUpgrade? LastUpgrade { get; private set; }
    /// <summary>这次打开时刚把已有数据的库升级过（做过升级前备份）。</summary>
    public bool UpgradedOnOpen { get; private set; }
    /// <summary>本次启动的升级编号：迁移写归档时带上它，升级结束按它汇总本次另存了几条。</summary>
    internal string UpgradeId { get; } = Guid.NewGuid().ToString("N");
    private readonly string _marker;
    private readonly string _connectionString;
    private readonly ISecretProtector _protector;
    private readonly IDeviceIdGenerator _deviceIds;

    /// <summary>打开或首次创建实例数据库：用按数据目录命名的跨进程互斥量（最多等 15 秒）串行化初始化；实例标记与数据库必须同时存在或同时缺失，否则拒绝启动而不是重开首次设置；随后检查库版本与完整性，再按顺序执行全部迁移。作为单例由依赖注入创建，任何不一致都抛异常阻止启动。</summary>
    public RelayDatabase(RelayDataOptions options, ISecretProtector protector, IEnumerable<IDatabaseMigration> migrations, IDeviceIdGenerator deviceIds)
    {
        this._protector = protector;
        this._deviceIds = deviceIds;
        Home = System.IO.Path.GetFullPath(options.Home);
        State = System.IO.Path.Combine(Home, ".state");
        Path = System.IO.Path.Combine(State, "relay.db");
        _marker = System.IO.Path.Combine(Home, ".relay-instance");
        _connectionString = new SqliteConnectionStringBuilder
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
            if (File.Exists(_marker) != File.Exists(Path))
                throw new InvalidOperationException("实例标记与数据库不完整。请恢复本实例备份，不能自动重新开放首次设置。");
            var created = !File.Exists(_marker);
            if (created) Create();
            InstanceId = File.ReadAllText(_marker).Trim();
            if (!Guid.TryParseExact(InstanceId, "N", out _)) throw new InvalidOperationException("实例标记损坏，请恢复备份。");
            int version;
            using (var connection = Open())
            {
                version = Convert.ToInt32(Scalar(connection, null, "PRAGMA user_version"));
                if (version is < 1 or > CurrentVersion || (string?)Scalar(connection, null, "PRAGMA quick_check") != "ok")
                    throw new InvalidOperationException("数据库版本不支持或完整性检查失败，请恢复备份。");
                _ = ReadSettings(connection, null);
            }
            // 已有数据的库要升级时，第一个迁移之前先备份并校验；备份不成就不升级。刚建的空库没有数据可丢，不备份。
            var backup = !created && version < CurrentVersion ? Backup(version) : null;
            // 顺序固定为：旧数据导入、上游架构升级、用量架构升级、上游命名与全局代理升级、上游尝试记录升级、自动切换与按上游计价升级、上游导出升级、站点身份、上游停用与外键、整数主键。
            foreach (var migration in migrations.OrderBy(item => item.Order)) migration.Apply(this);
            using (var connection = Open())
            {
                // 刚建的库：自动切换默认关（它会把请求交给另一家可能计费的上游，要管理员自己打开）。迁移链照老库升级走一遍，v6 那条迁移的 DEFAULT 1 语义不动，
                // 所以只在这里、只对这次新建的库改一次；已有的库（含用户自己的设置）一行不碰。
                if (created) Execute(connection, null, "UPDATE upstream SET failover=0");
                if (backup is not null) { RecordUpgrade(connection, version, backup); UpgradedOnOpen = true; }
                LastUpgrade = Scalar(connection, null, "SELECT value FROM metadata WHERE key='last_upgrade'") is string json
                    ? JsonSerializer.Deserialize<DatabaseUpgrade>(json, ConfigurationFiles.Json) : null;
            }
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
        // 旧文件必须先通过校验；已有旧号直接沿用，损坏时连新实例标记也不创建。
        var initial = LegacyImport.ReadInitialConfiguration(State) ?? new RelayConfiguration { DeviceId = _deviceIds.Generate() };
        initial.Validate();
        InstanceId = Guid.NewGuid().ToString("N");
        // 标记在数据库之外；写到一半即停机也不会把旧实例误认为未初始化。
        using (var file = new FileStream(_marker, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            file.Write(Encoding.ASCII.GetBytes(InstanceId));
        var createString = new SqliteConnectionStringBuilder(_connectionString) { Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
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
        Execute(connection, transaction, "INSERT INTO settings VALUES(1,1,$json)", ("$json", RelaySettings.Serialize(initial)));
        var code = ConfigurationFiles.NewToken();
        Execute(connection, transaction, "INSERT INTO bootstrap VALUES(1,$hash,$protected)",
            ("$hash", ConfigurationFiles.Hash(code)), ("$protected", _protector.Protect(Encoding.UTF8.GetBytes(code))));
        transaction.Commit();
    }

    /// <summary>
    /// 升级前备份：用 SQLite 在线备份把整库（含 WAL 里还没写回的页）复制到 .state\upgrade-backups\时间-v旧版本-编号\relay.db，改成单文件日志模式，
    /// 再单独打开核对完整性、库版本和实例编号。任何一步失败都删掉半截备份并抛出 <see cref="InvalidOperationException"/>，调用方因此不会开始迁移。
    /// 返回备份文件相对数据目录的路径（不含本机用户名），读给管理员听。
    /// </summary>
    private string Backup(int version)
    {
        var folder = System.IO.Path.Combine(State, "upgrade-backups", DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-v" + version + "-" + UpgradeId[..8]);
        var file = System.IO.Path.Combine(folder, "relay.db");
        try
        {
            _protector.ProtectDirectory(folder);
            var target = new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();
            using (var source = Open())
            using (var copy = new SqliteConnection(target))
            {
                copy.Open(); source.BackupDatabase(copy);
                Execute(copy, null, "PRAGMA journal_mode=DELETE");
            }
            using (var check = new SqliteConnection(new SqliteConnectionStringBuilder(target) { Mode = SqliteOpenMode.ReadWrite }.ToString()))
            {
                check.Open();
                if ((string?)Scalar(check, null, "PRAGMA quick_check") != "ok" || Convert.ToInt32(Scalar(check, null, "PRAGMA user_version")) != version
                    || (string?)Scalar(check, null, "SELECT value FROM metadata WHERE key='instance_id'") != InstanceId)
                    throw new InvalidOperationException("备份核对不一致。");
            }
            return System.IO.Path.GetRelativePath(Home, file);
        }
        // 磁盘满、没有权限、备份核对不一致：都不升级。原始异常可能带完整路径（含用户名），只读出结论。
        catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
            // 半截备份删不掉不影响结论：库没动，下次启动会另起一个时间戳目录重新备份。
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            throw new InvalidOperationException($"数据库要从版本 {version} 升级到 {CurrentVersion}，但升级前备份没有做成，所以没有升级，原库没动。请检查数据目录所在磁盘的剩余空间和写入权限，再重新启动。");
        }
    }

    /// <summary>升级结束：按本次升级编号汇总各版迁移另存的条数，连同版本、备份位置写进 metadata 的 last_upgrade，覆盖上一次的记录。</summary>
    private void RecordUpgrade(SqliteConnection connection, int fromVersion, string backup)
    {
        var archived = new List<ArchivedRows>();
        if (Scalar(connection, null, "SELECT 1 FROM sqlite_master WHERE type='table' AND name='migration_archive'") is not null)
        {
            using var command = Command(connection, null, """
                SELECT schema_version,source_table,reason,COUNT(*) FROM migration_archive WHERE upgrade_id=$upgrade
                GROUP BY schema_version,source_table,reason ORDER BY schema_version,source_table,reason
                """, ("$upgrade", UpgradeId));
            using var reader = command.ExecuteReader();
            while (reader.Read()) archived.Add(new(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3)));
        }
        var upgrade = new DatabaseUpgrade(fromVersion, Convert.ToInt32(Scalar(connection, null, "PRAGMA user_version")), DateTimeOffset.UtcNow, backup, archived);
        // 升级结束时库已是当前版本：metadata 带整数主键与通用实体基类的列，写明列名，时刻与行版本照 EF 的口径补上（Unix 毫秒、8 字节随机）。
        Execute(connection, null, """
            INSERT INTO metadata(key,value,created_at,updated_at,row_version) VALUES('last_upgrade',$json,$now,$now,randomblob(8))
            ON CONFLICT(key) DO UPDATE SET value=excluded.value,updated_at=excluded.updated_at,row_version=excluded.row_version
            """, ("$json", JsonSerializer.Serialize(upgrade, ConfigurationFiles.Json)), ("$now", upgrade.At.ToUnixTimeMilliseconds()));
    }

    /// <summary>迁移链与升级前备份用的连接：已有数据库只以读写模式打开，文件遗失时绝不悄悄建空库；打开前后的核对与 EF Core 的连接拦截器是同一段代码。</summary>
    public SqliteConnection Open()
    {
        EnsureAvailable();
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            Verify(connection);
            return connection;
        }
        catch
        {
            // 未交付的连接由创建方释放，调用者不会获得半初始化连接。
            connection.Dispose(); throw;
        }
    }

    /// <summary>打开连接之前：数据库文件与库外的实例标记都还在、标记里仍是本实例的编号；不对就抛出，绝不连上别的实例或空库。</summary>
    internal void EnsureAvailable()
    {
        if (!File.Exists(Path) || !File.Exists(_marker) || File.ReadAllText(_marker).Trim() != InstanceId)
            throw new InvalidOperationException("实例数据不可用，请检查数据目录或恢复备份。");
    }

    /// <summary>打开连接之后：设完全同步（每次提交都落盘），再核对库里的实例编号与标记一致；不一致说明误用了别的实例的库或备份，抛出。</summary>
    /// <param name="connection">刚打开的连接。</param>
    internal void Verify(SqliteConnection connection)
    {
        Execute(connection, null, "PRAGMA synchronous=FULL;");
        if ((string?)Scalar(connection, null, "SELECT value FROM metadata WHERE key='instance_id'") != InstanceId)
            throw new InvalidOperationException("数据库不属于当前实例，请恢复配套备份。");
    }

    /// <summary>迁移链用：创建绑定到给定连接和事务的参数化命令，null 参数值转为数据库 NULL；所有 SQL 都经这里传参，避免拼接。返回的命令由调用方释放。</summary>
    internal static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
    /// <summary>迁移链用：执行一条不返回结果集的参数化 SQL，返回受影响行数；命令在方法内释放。</summary>
    internal static int Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    { using var command = Command(connection, transaction, sql, parameters); return command.ExecuteNonQuery(); }
    /// <summary>迁移链用：执行参数化查询并返回第一行第一列，数据库 NULL 或无结果都返回 null；命令在方法内释放。</summary>
    internal static object? Scalar(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    { using var command = Command(connection, transaction, sql, parameters); var value = command.ExecuteScalar(); return value is DBNull ? null : value; }

    /// <summary>迁移链用：在调用方连接或事务中读取并校验设置与版本（各版本的 settings 表都有这两列）；运行时读设置经 <see cref="RelaySettings"/> 实体。</summary>
    internal static SettingsSnapshot ReadSettings(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = Command(connection, transaction, "SELECT version,json FROM settings WHERE id=1");
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("实例配置缺失，请恢复配套备份。");
        return new(reader.GetInt64(0), RelaySettings.Deserialize(reader.GetString(1)));
    }
}
