using Microsoft.Data.Sqlite;
using static Infrastructure.Persistence.RelayDatabase;

namespace Infrastructure.Persistence;

/// <summary>
/// v9 → v10：七张实体表（管理员、使用者、令牌、已保存上游、单价、管理员会话、用量）的主键改成库生成的整数（SQLite 的 rowid 别名），表与表之间的引用全部改成指向整数主键的外键，
/// 活动上游行补上对已保存上游的外键。还有人在用的旧编号留成带唯一约束的属性列：已保存上游的 uid（官方账户目录按它命名）、用量的 request_id（请求编号）、
/// 会话的 token_hash（会话凭据的哈希）；管理员、使用者、令牌、单价的旧编号没有外部用途，不留列，逐条连同新号另存进归档表。整个升级在一个事务里，失败全部回滚。
/// </summary>
internal sealed class IntegerKeySchema(RelayDataOptions options) : IDatabaseMigration
{
    /// <summary>本版的库版本号。</summary>
    private const int Version = 10;

    /// <summary>换主键的表（父先子后）：旧主键列名、旧编号是否只能另存（不留属性列）。映射按旧 rowid 顺序编号，列表里的创建先后不变。</summary>
    private static readonly (string Table, string Key, bool Archive)[] _keyed =
    [
        ("administrators", "id", true), ("users", "id", true), ("tokens", "id", true), ("upstream_candidates", "id", false),
        ("usage_prices", "id", true), ("admin_sessions", "id", false), ("usage_requests", "request_id", false),
    ];

    /// <summary>非空的引用：子表、列、父表。对不上就是库已损坏（外键一直开着），整体回滚，不猜。</summary>
    private static readonly (string Table, string Column, string Parent)[] _required =
    [
        ("tokens", "user_id", "users"), ("admin_sessions", "admin_id", "administrators"), ("usage_prices", "candidate_id", "upstream_candidates"),
        ("usage_requests", "user_id", "users"), ("usage_requests", "token_id", "tokens"), ("usage_attempts", "request_id", "usage_requests"),
    ];

    /// <summary>可空的上游引用：对不上时照版本 9 的做法原值另存、引用置空，记录本身保留。</summary>
    private static readonly (string Table, string Select)[] _optional =
    [
        ("upstream", "json_object('id',id,'candidate_id',candidate_id)"),
        ("usage_requests", "json_object('request_id',request_id,'candidate_id',candidate_id)"),
        ("usage_attempts", "json_object('request_id',request_id,'attempt',attempt,'candidate_id',candidate_id)"),
    ];

    /// <summary>重建的九张表，删旧表时按这个顺序（子先父后），改名与外键检查也按它。</summary>
    private static readonly string[] _rebuilt =
        ["usage_attempts", "usage_requests", "admin_sessions", "usage_prices", "upstream", "upstream_candidates", "tokens", "users", "administrators"];

    /// <summary>新表定义（父先子后）：外键都指最终表名，改名后自然对上；不级联，只有尝试记录随用量一起删。</summary>
    private const string Create = """
        CREATE TABLE administrators_v10 (id INTEGER PRIMARY KEY, password_hash TEXT NOT NULL);
        CREATE TABLE users_v10 (id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE, display_name TEXT NOT NULL, enabled INTEGER NOT NULL CHECK(enabled IN (0,1)));
        CREATE TABLE tokens_v10 (id INTEGER PRIMARY KEY, user_id INTEGER NOT NULL REFERENCES users(id), version INTEGER NOT NULL,
            hash TEXT NOT NULL UNIQUE, revoked_at TEXT, UNIQUE(user_id,version));
        CREATE TABLE upstream_candidates_v10 (id INTEGER PRIMARY KEY, uid TEXT NOT NULL UNIQUE, expected_version INTEGER NOT NULL,
            options_json TEXT NOT NULL, credential BLOB NOT NULL, state TEXT NOT NULL, report_json TEXT,
            created_at INTEGER NOT NULL, test_started_at INTEGER, name TEXT NOT NULL DEFAULT '', exported_at INTEGER, deleted_at INTEGER);
        CREATE TABLE upstream_v10 (id INTEGER PRIMARY KEY CHECK(id=1), version INTEGER NOT NULL, mode TEXT NOT NULL,
            base_url TEXT NOT NULL, credential BLOB, options_json TEXT, report_json TEXT, candidate_id INTEGER REFERENCES upstream_candidates(id),
            failover INTEGER NOT NULL DEFAULT 1 CHECK(failover IN (0,1)));
        CREATE TABLE usage_prices_v10 (id INTEGER PRIMARY KEY, candidate_id INTEGER NOT NULL REFERENCES upstream_candidates(id),
            model TEXT NOT NULL, json TEXT NOT NULL, UNIQUE(candidate_id,model));
        CREATE TABLE admin_sessions_v10 (id INTEGER PRIMARY KEY, token_hash TEXT NOT NULL UNIQUE,
            admin_id INTEGER NOT NULL REFERENCES administrators(id), expires_at INTEGER NOT NULL);
        CREATE TABLE usage_requests_v10 (
            id INTEGER PRIMARY KEY,
            request_id TEXT NOT NULL UNIQUE,
            user_id INTEGER NOT NULL REFERENCES users(id),
            token_id INTEGER NOT NULL REFERENCES tokens(id),
            upstream_version INTEGER,
            request_started_at INTEGER NOT NULL,
            ended_at INTEGER,
            kind TEXT NOT NULL CHECK(kind IN ('inference','estimate','models')),
            model TEXT,
            outcome TEXT NOT NULL DEFAULT 'started',
            dispatched INTEGER NOT NULL DEFAULT 0,
            status_code INTEGER,
            upstream_status INTEGER,
            duration_ms INTEGER,
            completeness TEXT NOT NULL DEFAULT 'partial',
            input_tokens INTEGER, output_tokens INTEGER,
            cache_read_tokens INTEGER, cache_write_tokens INTEGER,
            price_json TEXT, amount TEXT, currency TEXT,
            money_source TEXT NOT NULL DEFAULT 'unknown', money_completeness TEXT NOT NULL DEFAULT 'unknown',
            candidate_id INTEGER REFERENCES upstream_candidates(id),
            upstream_name TEXT);
        CREATE TABLE usage_attempts_v10 (
            usage_request_id INTEGER NOT NULL REFERENCES usage_requests(id) ON DELETE CASCADE,
            attempt INTEGER NOT NULL CHECK(attempt >= 1),
            candidate_id INTEGER REFERENCES upstream_candidates(id),
            upstream_version INTEGER NOT NULL,
            started_at INTEGER NOT NULL,
            headers_ms INTEGER,
            duration_ms INTEGER NOT NULL,
            upstream_status INTEGER,
            upstream_request_id TEXT,
            error_type TEXT,
            error_message TEXT,
            connect_failure TEXT,
            disposition TEXT NOT NULL CHECK(disposition IN ('client','next')),
            upstream_name TEXT,
            PRIMARY KEY(usage_request_id, attempt));
        """;

    /// <summary>按旧编号映射出新号搬数据（父先子后）；可空的上游引用对不上时左连接得空，原值已先另存。</summary>
    private const string Copy = """
        INSERT INTO administrators_v10(id,password_hash)
            SELECT k.new,t.password_hash FROM administrators t JOIN temp.v10_keys k ON k.source='administrators' AND k.old=t.id;
        INSERT INTO users_v10(id,name,display_name,enabled)
            SELECT k.new,t.name,t.display_name,t.enabled FROM users t JOIN temp.v10_keys k ON k.source='users' AND k.old=t.id;
        INSERT INTO tokens_v10(id,user_id,version,hash,revoked_at)
            SELECT k.new,u.new,t.version,t.hash,t.revoked_at FROM tokens t JOIN temp.v10_keys k ON k.source='tokens' AND k.old=t.id
            JOIN temp.v10_keys u ON u.source='users' AND u.old=t.user_id;
        INSERT INTO upstream_candidates_v10(id,uid,expected_version,options_json,credential,state,report_json,created_at,test_started_at,name,exported_at,deleted_at)
            SELECT k.new,t.id,t.expected_version,t.options_json,t.credential,t.state,t.report_json,t.created_at,t.test_started_at,t.name,t.exported_at,t.deleted_at
            FROM upstream_candidates t JOIN temp.v10_keys k ON k.source='upstream_candidates' AND k.old=t.id;
        INSERT INTO upstream_v10(id,version,mode,base_url,credential,options_json,report_json,candidate_id,failover)
            SELECT t.id,t.version,t.mode,t.base_url,t.credential,t.options_json,t.report_json,c.new,t.failover
            FROM upstream t LEFT JOIN temp.v10_keys c ON c.source='upstream_candidates' AND c.old=t.candidate_id;
        INSERT INTO usage_prices_v10(id,candidate_id,model,json)
            SELECT k.new,c.new,t.model,t.json FROM usage_prices t JOIN temp.v10_keys k ON k.source='usage_prices' AND k.old=t.id
            JOIN temp.v10_keys c ON c.source='upstream_candidates' AND c.old=t.candidate_id;
        INSERT INTO admin_sessions_v10(id,token_hash,admin_id,expires_at)
            SELECT k.new,t.id,a.new,t.expires_at FROM admin_sessions t JOIN temp.v10_keys k ON k.source='admin_sessions' AND k.old=t.id
            JOIN temp.v10_keys a ON a.source='administrators' AND a.old=t.admin_id;
        INSERT INTO usage_requests_v10(id,request_id,user_id,token_id,upstream_version,request_started_at,ended_at,kind,model,outcome,dispatched,
                status_code,upstream_status,duration_ms,completeness,input_tokens,output_tokens,cache_read_tokens,cache_write_tokens,
                price_json,amount,currency,money_source,money_completeness,candidate_id,upstream_name)
            SELECT k.new,t.request_id,u.new,o.new,t.upstream_version,t.request_started_at,t.ended_at,t.kind,t.model,t.outcome,t.dispatched,
                t.status_code,t.upstream_status,t.duration_ms,t.completeness,t.input_tokens,t.output_tokens,t.cache_read_tokens,t.cache_write_tokens,
                t.price_json,t.amount,t.currency,t.money_source,t.money_completeness,c.new,t.upstream_name
            FROM usage_requests t JOIN temp.v10_keys k ON k.source='usage_requests' AND k.old=t.request_id
            JOIN temp.v10_keys u ON u.source='users' AND u.old=t.user_id
            JOIN temp.v10_keys o ON o.source='tokens' AND o.old=t.token_id
            LEFT JOIN temp.v10_keys c ON c.source='upstream_candidates' AND c.old=t.candidate_id;
        INSERT INTO usage_attempts_v10(usage_request_id,attempt,candidate_id,upstream_version,started_at,headers_ms,duration_ms,upstream_status,
                upstream_request_id,error_type,error_message,connect_failure,disposition,upstream_name)
            SELECT r.new,t.attempt,c.new,t.upstream_version,t.started_at,t.headers_ms,t.duration_ms,t.upstream_status,
                t.upstream_request_id,t.error_type,t.error_message,t.connect_failure,t.disposition,t.upstream_name
            FROM usage_attempts t JOIN temp.v10_keys r ON r.source='usage_requests' AND r.old=t.request_id
            LEFT JOIN temp.v10_keys c ON c.source='upstream_candidates' AND c.old=t.candidate_id;
        """;

    /// <summary>重建后的索引：与版本 9 同名同列，查询与分页顺序不变。</summary>
    private const string Indexes = """
        CREATE UNIQUE INDEX one_administrator ON administrators((1));
        CREATE UNIQUE INDEX one_active_token ON tokens(user_id) WHERE revoked_at IS NULL;
        CREATE INDEX usage_time ON usage_requests(request_started_at,request_id);
        CREATE INDEX usage_user_time ON usage_requests(user_id,request_started_at,request_id);
        CREATE INDEX usage_kind_time ON usage_requests(kind,request_started_at);
        CREATE INDEX usage_model_time ON usage_requests(model,request_started_at);
        CREATE INDEX usage_outcome_time ON usage_requests(outcome,request_started_at);
        CREATE INDEX usage_attempts_candidate ON usage_attempts(candidate_id,started_at);
        """;

    /// <summary>排在最后（100）：依赖上游停用与外键升级已把库带到版本 9。</summary>
    public int Order => 100;

    /// <summary>
    /// 已是 10 时直接返回，不是 9 时拒绝。照 SQLite「ALTER TABLE 12 步」：事务外关外键检查；事务内先按旧 rowid 顺序给每张表的旧编号编出新号（临时映射表），
    /// 核对非空引用全都对得上（对不上就回滚），可空的上游引用对不上的原值另存，四张不留旧编号的表逐条另存旧编号与新号；
    /// 然后建新表、搬数据、删旧表、改名、建索引，核对九张表条数与升级前一致、外键检查 0 条，有一项不过就回滚。提交后连接释放，下一个连接照常开外键检查。
    /// </summary>
    public void Apply(RelayDatabase database)
    {
        using var connection = database.Open();
        var version = Convert.ToInt32(Scalar(connection, null, "PRAGMA user_version"));
        if (version >= Version) return;
        if (version != 9) throw new InvalidOperationException("数据库版本不支持。");
        // 外键开关在事务里设置无效，必须在开事务前关；否则删旧用量表会按 ON DELETE CASCADE 连带删光尝试记录。
        Execute(connection, null, "PRAGMA foreign_keys=OFF");
        using var transaction = connection.BeginTransaction();
        MigrationArchive.Ensure(connection, transaction);
        Execute(connection, transaction, "CREATE TEMP TABLE v10_keys (source TEXT NOT NULL, old TEXT NOT NULL, new INTEGER NOT NULL, PRIMARY KEY(source,old))");
        foreach (var (table, key, _) in _keyed)
            Execute(connection, transaction, $"INSERT INTO temp.v10_keys(source,old,new) SELECT '{table}',{key},ROW_NUMBER() OVER (ORDER BY rowid) FROM {table}");
        foreach (var (table, column, parent) in _required)
            if (Convert.ToInt64(Scalar(connection, transaction,
                $"SELECT COUNT(*) FROM {table} WHERE {column} IS NULL OR {column} NOT IN (SELECT old FROM temp.v10_keys WHERE source='{parent}')")) is var broken and not 0)
                throw new InvalidOperationException($"升级到版本 {Version} 前发现 {table} 表有 {broken} 条记录引用的 {parent} 不存在，库已损坏，已整体回滚。请恢复配套备份，或保留数据目录并联系维护者。");
        foreach (var (table, select) in _optional)
            MigrationArchive.AddRows(connection, transaction, database, Version, table, "orphan-reference",
                $"SELECT {select} AS row_json FROM {table} WHERE candidate_id IS NOT NULL AND candidate_id NOT IN (SELECT old FROM temp.v10_keys WHERE source='upstream_candidates')");
        foreach (var (table, _, _) in _keyed.Where(item => item.Archive))
            MigrationArchive.AddRows(connection, transaction, database, Version, table, "replaced-id",
                $"SELECT json_object('id',old,'new_id',new) AS row_json FROM temp.v10_keys WHERE source='{table}' ORDER BY new");
        var before = _rebuilt.ToDictionary(table => table, table => Convert.ToInt64(Scalar(connection, transaction, $"SELECT COUNT(*) FROM {table}")));
        Execute(connection, transaction, Create);
        Execute(connection, transaction, Copy);
        foreach (var table in _rebuilt) Execute(connection, transaction, $"DROP TABLE {table}");
        // 旧表已全部删掉，改名先后不影响外键：新表里的外键本来就写的最终表名。
        foreach (var table in _rebuilt) Execute(connection, transaction, $"ALTER TABLE {table}_v10 RENAME TO {table}");
        Execute(connection, transaction, Indexes + "DROP TABLE temp.v10_keys;");
        foreach (var table in _rebuilt)
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
