using Microsoft.Data.Sqlite;

namespace TodoSync;

/// <summary>
/// SQLite 数据访问——HTTP 层（Program）只经这里触库（v1.5 重构：库操作与路由分离）。
/// 单用户低并发：调用方用全局锁串行化写事务即可。
/// </summary>
public static class Store
{
    public static SqliteConnection OpenDb(string path)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString());
        conn.Open();
        return conn;
    }

    public static void Init(SqliteConnection db)
    {
        Exec(db, "PRAGMA journal_mode=WAL");

        // todos：检测旧架构（无 space_id 列/PK=id）→ 重建为 (space_id,id) 复合主键，
        // 存量数据全部归入 default 空间（服务端是同步枢纽，条目可由客户端重推，墓碑保留防复活）
        var todosExists = TableExists(db, "todos");
        var todosHasSpace = todosExists && ColumnExists(db, "todos", "space_id");
        if (todosExists && !todosHasSpace)
        {
            Exec(db, "DROP INDEX IF EXISTS ix_todos_version");
            Exec(db, "DROP INDEX IF EXISTS ix_todos_space");
            Exec(db, """
                CREATE TABLE todos_new(
                    space_id TEXT NOT NULL DEFAULT 'default',
                    id TEXT NOT NULL,
                    json TEXT NOT NULL,
                    updated_at INTEGER NOT NULL,
                    deleted_at INTEGER,
                    version INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY(space_id,id))
                """);
            Exec(db, """
                INSERT INTO todos_new(space_id,id,json,updated_at,deleted_at,version)
                SELECT 'default',id,json,updated_at,deleted_at,version FROM todos
                """);
            Exec(db, "DROP TABLE todos");
            Exec(db, "ALTER TABLE todos_new RENAME TO todos");
        }
        Exec(db, """
            CREATE TABLE IF NOT EXISTS todos(
                space_id TEXT NOT NULL DEFAULT 'default',
                id TEXT NOT NULL,
                json TEXT NOT NULL,
                updated_at INTEGER NOT NULL,
                deleted_at INTEGER,
                version INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(space_id,id))
            """);
        Exec(db, "CREATE INDEX IF NOT EXISTS ix_todos_space ON todos(space_id)");
        Exec(db, "CREATE INDEX IF NOT EXISTS ix_todos_version ON todos(version)");

        Exec(db, """
            CREATE TABLE IF NOT EXISTS devices(
                token_hash TEXT PRIMARY KEY,
                name TEXT NOT NULL DEFAULT '',
                paired_at INTEGER NOT NULL,
                last_seen INTEGER NOT NULL DEFAULT 0)
            """);
        if (TableExists(db, "devices") && !ColumnExists(db, "devices", "space_id"))
            Exec(db, "ALTER TABLE devices ADD COLUMN space_id TEXT NOT NULL DEFAULT 'default'");

        Exec(db, "CREATE TABLE IF NOT EXISTS meta(k TEXT PRIMARY KEY, v TEXT NOT NULL)");
    }

    private static bool TableExists(SqliteConnection db, string name)
    {
        using var q = db.CreateCommand();
        q.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@n";
        q.Parameters.AddWithValue("@n", name);
        return Convert.ToInt64(q.ExecuteScalar()) > 0;
    }

    private static bool ColumnExists(SqliteConnection db, string table, string column)
    {
        using var q = db.CreateCommand();
        q.CommandText = "SELECT COUNT(*) FROM pragma_table_info(@t) WHERE name=@c";
        q.Parameters.AddWithValue("@t", table);
        q.Parameters.AddWithValue("@c", column);
        return Convert.ToInt64(q.ExecuteScalar()) > 0;
    }

    public static void Exec(SqliteConnection db, string sql, params (string Name, object? Value)[] ps)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public static long GetVersion(SqliteConnection db)
    {
        using var q = db.CreateCommand();
        q.CommandText = "SELECT v FROM meta WHERE k='version'";
        return q.ExecuteScalar() is string s ? long.Parse(s) : 0;
    }

    public static void SetVersion(SqliteConnection db, long v)
        => Exec(db, "INSERT INTO meta(k,v) VALUES('version',@v) ON CONFLICT(k) DO UPDATE SET v=@v", ("@v", v));

    public static (string? Code, long Exp, string Space) GetPairSpace(SqliteConnection db)
    {
        using var q = db.CreateCommand();
        q.CommandText = "SELECT k,v FROM meta WHERE k IN ('pair_code','pair_expiry','pair_space')";
        string? code = null;
        long exp = 0;
        var space = "default";
        using var r = q.ExecuteReader();
        while (r.Read())
        {
            var k = r.GetString(0);
            if (k == "pair_code") code = r.GetString(1);
            else if (k == "pair_space") space = r.GetString(1);
            else exp = long.Parse(r.GetString(1));
        }
        return (code, exp, space);
    }

    public static void ClearPair(SqliteConnection db)
        => Exec(db, "DELETE FROM meta WHERE k IN ('pair_code','pair_expiry')");

    public static void SetPair(SqliteConnection db, string code, long exp, string space)
    {
        Exec(db, "INSERT INTO meta(k,v) VALUES('pair_code',@c) ON CONFLICT(k) DO UPDATE SET v=@c", ("@c", code));
        Exec(db, "INSERT INTO meta(k,v) VALUES('pair_expiry',@e) ON CONFLICT(k) DO UPDATE SET v=@e", ("@e", exp));
        Exec(db, "INSERT INTO meta(k,v) VALUES('pair_space',@s) ON CONFLICT(k) DO UPDATE SET v=@s", ("@s", space));
    }

    public static long? TodoUpdatedAt(SqliteConnection db, string id)
    {
        using var q = db.CreateCommand();
        q.CommandText = "SELECT updated_at FROM todos WHERE id=@id";
        q.Parameters.AddWithValue("@id", id);
        var cur = q.ExecuteScalar();
        return cur is null ? null : Convert.ToInt64(cur);
    }

    /// <summary>墓碑（deleted_at 非空）不覆盖原 json；普通条目整体覆盖。</summary>
    public static void UpsertTodo(SqliteConnection db, string space, string id, string json,
        long updatedAt, long? deletedAt, long version)
    {
        if (deletedAt is null)
            Exec(db, """
                INSERT INTO todos(space_id,id,json,updated_at,deleted_at,version)
                VALUES(@s,@id,@j,@u,NULL,@v)
                ON CONFLICT(space_id,id) DO UPDATE SET json=@j, updated_at=@u, deleted_at=NULL, version=@v
                """,
                ("@s", space), ("@id", id), ("@j", json), ("@u", updatedAt), ("@v", version));
        else
            Exec(db, """
                INSERT INTO todos(space_id,id,json,updated_at,deleted_at,version)
                VALUES(@s,@id,@j,@u,@d,@v)
                ON CONFLICT(space_id,id) DO UPDATE SET updated_at=@u, deleted_at=@d, version=@v
                """,
                ("@s", space), ("@id", id), ("@j", json), ("@u", updatedAt), ("@d", deletedAt), ("@v", version));
    }

    /// <summary>since 之后（含墓碑）的全部变更行，按版本升序。</summary>
    public static List<(string Id, string Json, long UpdatedAt, long? DeletedAt)> ChangesSince(
        SqliteConnection db, string space, long since)
    {
        var list = new List<(string, string, long, long?)>();
        using var q = db.CreateCommand();
        q.CommandText = "SELECT id,json,updated_at,deleted_at FROM todos WHERE space_id=@sp AND version>@s ORDER BY version";
        q.Parameters.AddWithValue("@sp", space);
        q.Parameters.AddWithValue("@s", since);
        using var r = q.ExecuteReader();
        while (r.Read())
            list.Add((r.GetString(0), r.GetString(1), r.GetInt64(2),
                r.IsDBNull(3) ? null : r.GetInt64(3)));
        return list;
    }

    /// <summary>设备令牌心跳：刷新 last_seen 并返回所属空间（null=令牌不存在）。</summary>
    public static string? TouchDeviceSpace(SqliteConnection db, string hash, long now)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE devices SET last_seen=@n WHERE token_hash=@h RETURNING space_id";
        cmd.Parameters.AddWithValue("@n", now);
        cmd.Parameters.AddWithValue("@h", hash);
        return cmd.ExecuteScalar() as string;
    }

    public static void InsertDevice(SqliteConnection db, string hash, string name, string space, long now)
        => Exec(db, "INSERT OR REPLACE INTO devices(token_hash,name,paired_at,last_seen,space_id) VALUES(@h,@n,@p,@p,@s)",
            ("@h", hash), ("@n", name), ("@p", now), ("@s", space));

    public static List<(string Hash, string Name, long PairedAt, long LastSeen)> Devices(SqliteConnection db)
    {
        var list = new List<(string, string, long, long)>();
        using var q = db.CreateCommand();
        q.CommandText = "SELECT token_hash,name,paired_at,last_seen FROM devices ORDER BY paired_at";
        using var r = q.ExecuteReader();
        while (r.Read())
            list.Add((r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3)));
        return list;
    }

    public static void DeleteDevice(SqliteConnection db, string hash)
        => Exec(db, "DELETE FROM devices WHERE token_hash=@h", ("@h", hash));

    /// <summary>运维视图：各空间的条目数（含墓碑）。</summary>
    public static List<(string Space, int Count)> SpaceStats(SqliteConnection db)
    {
        var list = new List<(string, int)>();
        using var q = db.CreateCommand();
        q.CommandText = "SELECT space_id, COUNT(*) FROM todos GROUP BY space_id ORDER BY space_id";
        using var r = q.ExecuteReader();
        while (r.Read()) list.Add((r.GetString(0), r.GetInt32(1)));
        return list;
    }
}
