using static Infrastructure.Persistence.RelayDatabase;

namespace Infrastructure.Persistence;

/// <summary>
/// v10 → v11：运行时改由 EF Core 与通用仓储存取，每张表补齐通用实体基类的八列（创建与更新时刻和来源、软删除标记与时刻和来源、行版本）。
/// 复合主键的 usage_attempts、按名字存的 metadata、secrets、public_assets、按操作编号存的 site_identity_operations 改成库生成的整数主键，原来的键留成唯一列；
/// site_identity 对维护操作的引用改成指向整数主键的外键。upstream_candidates 已有的 created_at、deleted_at（Unix 毫秒）就是新的同名列，旧值不变，is_deleted 按 deleted_at 回填。
/// 只加列、换主键，不丢数据，所以没有要另存的行；整个升级在一个事务里，任何一张表条数或外键对不上都整体回滚。migration_archive 只有迁移链读写，不动。
/// </summary>
internal sealed class EntityBaseSchema(RelayDataOptions options) : IDatabaseMigration
{
    /// <summary>本版的库版本号。</summary>
    private const int Version = 11;

    /// <summary>每张表末尾补的通用实体基类八列；时刻是 Unix 毫秒，行版本由 EF Core 每次写入时换新。</summary>
    private const string Base = """
        created_at INTEGER NOT NULL DEFAULT 0, created_by TEXT, updated_at INTEGER NOT NULL DEFAULT 0, updated_by TEXT,
        is_deleted INTEGER NOT NULL DEFAULT 0 CHECK(is_deleted IN (0,1)), deleted_at INTEGER, deleted_by TEXT, row_version BLOB
        """;

    /// <summary>UTC 刻度换成 Unix 毫秒的 SQL 片段前缀：(刻度 - 1970 年的刻度) / 10000。</summary>
    private const string TicksToMilliseconds = "-621355968000000000)/10000";

    /// <summary>
    /// 重建的表（父先子后）：表名、新表定义（建成 <c>表名_v11</c>，外键写最终表名）、搬数据的列清单（新表列 ← 旧表表达式，基类列另外统一补）、
    /// 创建时刻的来源表达式（没有来源的取升级时刻 <c>$now</c>）。
    /// </summary>
    private static readonly (string Table, string Columns, string Target, string Source, string Created)[] _tables =
    [
        ("metadata", "id INTEGER PRIMARY KEY, key TEXT NOT NULL UNIQUE, value TEXT NOT NULL", "key,value", "key,value", "$now"),
        ("settings", "id INTEGER PRIMARY KEY CHECK(id=1), version INTEGER NOT NULL, json TEXT NOT NULL", "id,version,json", "id,version,json", "$now"),
        ("bootstrap", "id INTEGER PRIMARY KEY CHECK(id=1), hash TEXT NOT NULL, protected BLOB NOT NULL", "id,hash,protected", "id,hash,protected", "$now"),
        ("secrets", "id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE, protected BLOB NOT NULL", "name,protected", "name,protected", "$now"),
        ("public_assets", "id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE, value TEXT NOT NULL", "name,value", "name,value", "$now"),
        ("administrators", "id INTEGER PRIMARY KEY, password_hash TEXT NOT NULL", "id,password_hash", "id,password_hash", "$now"),
        ("users", "id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE, display_name TEXT NOT NULL, enabled INTEGER NOT NULL CHECK(enabled IN (0,1))",
            "id,name,display_name,enabled", "id,name,display_name,enabled", "$now"),
        ("tokens", "id INTEGER PRIMARY KEY, user_id INTEGER NOT NULL REFERENCES users(id), version INTEGER NOT NULL, hash TEXT NOT NULL UNIQUE, revoked_at TEXT, "
            + Base + ", UNIQUE(user_id,version)", "id,user_id,version,hash,revoked_at", "id,user_id,version,hash,revoked_at", "$now"),
        ("upstream_candidates", "id INTEGER PRIMARY KEY, uid TEXT NOT NULL UNIQUE, expected_version INTEGER NOT NULL, options_json TEXT NOT NULL, credential BLOB NOT NULL, "
            + "state TEXT NOT NULL, report_json TEXT, test_started_at INTEGER, name TEXT NOT NULL DEFAULT '', exported_at INTEGER",
            "id,uid,expected_version,options_json,credential,state,report_json,test_started_at,name,exported_at",
            "id,uid,expected_version,options_json,credential,state,report_json,test_started_at,name,exported_at", "created_at"),
        ("upstream", "id INTEGER PRIMARY KEY CHECK(id=1), version INTEGER NOT NULL, mode TEXT NOT NULL, base_url TEXT NOT NULL, credential BLOB, options_json TEXT, "
            + "report_json TEXT, candidate_id INTEGER REFERENCES upstream_candidates(id), failover INTEGER NOT NULL DEFAULT 1 CHECK(failover IN (0,1))",
            "id,version,mode,base_url,credential,options_json,report_json,candidate_id,failover", "id,version,mode,base_url,credential,options_json,report_json,candidate_id,failover", "$now"),
        ("usage_prices", "id INTEGER PRIMARY KEY, candidate_id INTEGER NOT NULL REFERENCES upstream_candidates(id), model TEXT NOT NULL, json TEXT NOT NULL, "
            + Base + ", UNIQUE(candidate_id,model)", "id,candidate_id,model,json", "id,candidate_id,model,json", "$now"),
        ("admin_sessions", "id INTEGER PRIMARY KEY, token_hash TEXT NOT NULL UNIQUE, admin_id INTEGER NOT NULL REFERENCES administrators(id), expires_at INTEGER NOT NULL",
            "id,token_hash,admin_id,expires_at", "id,token_hash,admin_id,expires_at", "$now"),
        ("usage_requests", """
            id INTEGER PRIMARY KEY, request_id TEXT NOT NULL UNIQUE, user_id INTEGER NOT NULL REFERENCES users(id), token_id INTEGER NOT NULL REFERENCES tokens(id),
            upstream_version INTEGER, request_started_at INTEGER NOT NULL, ended_at INTEGER, kind TEXT NOT NULL CHECK(kind IN ('inference','estimate','models')),
            model TEXT, outcome TEXT NOT NULL DEFAULT 'started', dispatched INTEGER NOT NULL DEFAULT 0, status_code INTEGER, upstream_status INTEGER, duration_ms INTEGER,
            completeness TEXT NOT NULL DEFAULT 'partial', input_tokens INTEGER, output_tokens INTEGER, cache_read_tokens INTEGER, cache_write_tokens INTEGER,
            price_json TEXT, amount TEXT, currency TEXT, money_source TEXT NOT NULL DEFAULT 'unknown', money_completeness TEXT NOT NULL DEFAULT 'unknown',
            candidate_id INTEGER REFERENCES upstream_candidates(id), upstream_name TEXT
            """, Usage, Usage, "(request_started_at" + TicksToMilliseconds),
        ("usage_attempts", """
            id INTEGER PRIMARY KEY, usage_request_id INTEGER NOT NULL REFERENCES usage_requests(id) ON DELETE CASCADE, attempt INTEGER NOT NULL CHECK(attempt >= 1),
            candidate_id INTEGER REFERENCES upstream_candidates(id), upstream_version INTEGER NOT NULL, started_at INTEGER NOT NULL, headers_ms INTEGER,
            duration_ms INTEGER NOT NULL, upstream_status INTEGER, upstream_request_id TEXT, error_type TEXT, error_message TEXT, connect_failure TEXT,
            disposition TEXT NOT NULL CHECK(disposition IN ('client','next')), upstream_name TEXT
            """ + ", " + Base + ", UNIQUE(usage_request_id,attempt)", Attempts, Attempts, "(started_at" + TicksToMilliseconds),
        ("site_identity_operations", "id INTEGER PRIMARY KEY, operation_id TEXT NOT NULL UNIQUE, version INTEGER NOT NULL UNIQUE CHECK(version >= 2), "
            + "pending INTEGER NOT NULL CHECK(pending IN (0,1))", "operation_id,version,pending", "operation_id,version,pending", "$now"),
        ("site_identity", "id INTEGER PRIMARY KEY CHECK(id=1), version INTEGER NOT NULL CHECK(version >= 1), "
            + "site_identity_operation_id INTEGER REFERENCES site_identity_operations(id), pending INTEGER NOT NULL CHECK(pending IN (0,1))",
            "id,version,site_identity_operation_id,pending",
            "id,version,(SELECT n.id FROM site_identity_operations_v11 n WHERE n.operation_id=site_identity.operation_id),pending", "$now"),
    ];

    /// <summary>用量表除基类列以外的全部列（新旧同名）。</summary>
    private const string Usage = "id,request_id,user_id,token_id,upstream_version,request_started_at,ended_at,kind,model,outcome,dispatched,status_code,upstream_status,"
        + "duration_ms,completeness,input_tokens,output_tokens,cache_read_tokens,cache_write_tokens,price_json,amount,currency,money_source,money_completeness,candidate_id,upstream_name";

    /// <summary>尝试表除整数主键与基类列以外的全部列（新旧同名）。</summary>
    private const string Attempts = "usage_request_id,attempt,candidate_id,upstream_version,started_at,headers_ms,duration_ms,upstream_status,"
        + "upstream_request_id,error_type,error_message,connect_failure,disposition,upstream_name";

    /// <summary>
    /// 重建后的索引：前八个与版本 10 同名同列，查询与分页顺序不变；后五个是新加的外键列索引（没有被其他索引的首列覆盖的外键），
    /// 名字取 EF Core 按约定给外键建的索引名，与模型逐一对上，按外键查找与删除前的外键核对不再扫全表。
    /// </summary>
    private const string Indexes = """
        CREATE UNIQUE INDEX one_administrator ON administrators((1));
        CREATE UNIQUE INDEX one_active_token ON tokens(user_id) WHERE revoked_at IS NULL;
        CREATE INDEX usage_time ON usage_requests(request_started_at,request_id);
        CREATE INDEX usage_user_time ON usage_requests(user_id,request_started_at,request_id);
        CREATE INDEX usage_kind_time ON usage_requests(kind,request_started_at);
        CREATE INDEX usage_model_time ON usage_requests(model,request_started_at);
        CREATE INDEX usage_outcome_time ON usage_requests(outcome,request_started_at);
        CREATE INDEX usage_attempts_candidate ON usage_attempts(candidate_id,started_at);
        CREATE INDEX IX_admin_sessions_admin_id ON admin_sessions(admin_id);
        CREATE INDEX IX_upstream_candidate_id ON upstream(candidate_id);
        CREATE INDEX IX_usage_requests_token_id ON usage_requests(token_id);
        CREATE INDEX IX_usage_requests_candidate_id ON usage_requests(candidate_id);
        CREATE INDEX IX_site_identity_site_identity_operation_id ON site_identity(site_identity_operation_id);
        """;

    /// <summary>排在最后（110）：依赖整数主键升级已把库带到版本 10。</summary>
    public int Order => 110;

    /// <summary>
    /// 已是 11 时直接返回，不是 10 时拒绝。照 SQLite「ALTER TABLE 12 步」：事务外关外键检查；事务内记下每张表的条数，建新表、按旧行顺序搬数据并补基类列
    /// （创建与更新时刻取行里已有的时刻，没有的取升级时刻；行版本给每行一个随机值），删旧表（子先父后）、改名、建索引，
    /// 再核对每张表条数与升级前一致、外键检查 0 条，有一项不过就回滚。提交后连接释放，下一个连接照常开外键检查。
    /// </summary>
    public void Apply(RelayDatabase database)
    {
        using var connection = database.Open();
        var version = Convert.ToInt32(Scalar(connection, null, "PRAGMA user_version"));
        if (version >= Version) return;
        if (version != 10) throw new InvalidOperationException("数据库版本不支持。");
        // 外键开关在事务里设置无效，必须在开事务前关；否则删旧用量表会按 ON DELETE CASCADE 连带删光尝试记录。
        Execute(connection, null, "PRAGMA foreign_keys=OFF");
        using var transaction = connection.BeginTransaction();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var before = _tables.ToDictionary(item => item.Table, item => Convert.ToInt64(Scalar(connection, transaction, $"SELECT COUNT(*) FROM {item.Table}")));
        foreach (var (table, columns, target, source, created) in _tables)
        {
            // 定义里已带 UNIQUE(...) 表约束的，基类列已经插在表约束之前；其余在末尾补。
            var definition = columns.Contains(Base, StringComparison.Ordinal) ? columns : columns + ", " + Base;
            Execute(connection, transaction, $"CREATE TABLE {table}_v11 ({definition})");
            var deleted = table == "upstream_candidates" ? "deleted_at IS NOT NULL,deleted_at" : "0,NULL";
            Execute(connection, transaction, $"""
                INSERT INTO {table}_v11({target},created_at,updated_at,is_deleted,deleted_at,row_version)
                SELECT {source},{created},{created},{deleted},randomblob(8) FROM {table} ORDER BY rowid
                """, ("$now", now));
        }
        // 先删子表再删父表；新表的外键本来就写的最终表名，改名后自然对上。
        foreach (var (table, _, _, _, _) in _tables.Reverse()) Execute(connection, transaction, $"DROP TABLE {table}");
        foreach (var (table, _, _, _, _) in _tables) Execute(connection, transaction, $"ALTER TABLE {table}_v11 RENAME TO {table}");
        Execute(connection, transaction, Indexes);
        foreach (var (table, _, _, _, _) in _tables)
        {
            if (Convert.ToInt64(Scalar(connection, transaction, $"SELECT COUNT(*) FROM {table}")) is var count && count != before[table])
                throw new InvalidOperationException($"升级到版本 {Version} 时 {table} 表搬完是 {count} 条、原来是 {before[table]} 条，已整体回滚，请保留数据目录并联系维护者。");
            if (Scalar(connection, transaction, $"SELECT COUNT(*) FROM pragma_foreign_key_check('{table}')") is long broken && broken != 0)
                throw new InvalidOperationException($"升级到版本 {Version} 后 {table} 表仍有 {broken} 条外键对不上，已整体回滚，请保留数据目录并联系维护者。");
        }
        options.MigrationCheckpoint?.Invoke();
        Execute(connection, transaction, $"PRAGMA user_version={Version}");
        transaction.Commit();
    }
}
