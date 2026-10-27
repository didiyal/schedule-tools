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
        Exec(db, """
            CREATE TABLE IF NOT EXISTS todos(
                id TEXT PRIMARY KEY,
                json TEXT NOT NULL,
                updated_at INTEGER NOT NULL,
                deleted_at INTEGER,
                version INTEGER NOT NULL DEFAULT 0)
            """);
        Exec(db, "CREATE INDEX IF NOT EXISTS ix_todos_version ON todos(version)");
        Exec(db, """
            CREATE TABLE IF NOT EXISTS devices(
                token_hash TEXT PRIMARY KEY,
                name TEXT NOT NULL DEFAULT '',
                paired_at INTEGER NOT NULL,
                last_seen INTEGER NOT NULL DEFAULT 0)
            """);
        Exec(db, "CREATE TABLE IF NOT EXISTS meta(k TEXT PRIMARY KEY, v TEXT NOT NULL)");
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

    public static (string? Code, long Exp) GetPair(SqliteConnection db)
    {
        using var q = db.CreateCommand();
        q.CommandText = "SELECT k,v FROM meta WHERE k IN ('pair_code','pair_expiry')";
        string? code = null;
        long exp = 0;
        using var r = q.ExecuteReader();
        while (r.Read())
            if (r.GetString(0) == "pair_code") code = r.GetString(1);
            else exp = long.Parse(r.GetString(1));
        return (code, exp);
    }

    public static void ClearPair(SqliteConnection db)
        => Exec(db, "DELETE FROM meta WHERE k IN ('pair_code','pair_expiry')");

    public static void SetPair(SqliteConnection db, string code, long exp)
    {
        Exec(db, "INSERT INTO meta(k,v) VALUES('pair_code',@c) ON CONFLICT(k) DO UPDATE SET v=@c", ("@c", code));
        Exec(db, "INSERT INTO meta(k,v) VALUES('pair_expiry',@e) ON CONFLICT(k) DO UPDATE SET v=@e", ("@e", exp));
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
    public static void UpsertTodo(SqliteConnection db, string id, string json,
        long updatedAt, long? deletedAt, long version)
    {
        if (deletedAt is null)
            Exec(db, """
                INSERT INTO todos(id,json,updated_at,deleted_at,version) VALUES(@id,@j,@u,NULL,@v)
                ON CONFLICT(id) DO UPDATE SET json=@j, updated_at=@u, deleted_at=NULL, version=@v
                """,
                ("@id", id), ("@j", json), ("@u", updatedAt), ("@v", version));
        else
            Exec(db, """
                INSERT INTO todos(id,json,updated_at,deleted_at,version) VALUES(@id,@j,@u,@d,@v)
                ON CONFLICT(id) DO UPDATE SET updated_at=@u, deleted_at=@d, version=@v
                """,
                ("@id", id), ("@j", json), ("@u", updatedAt), ("@d", deletedAt), ("@v", version));
    }

    /// <summary>since 之后（含墓碑）的全部变更行，按版本升序。</summary>
    public static List<(string Id, string Json, long UpdatedAt, long? DeletedAt)> ChangesSince(
        SqliteConnection db, long since)
    {
        var list = new List<(string, string, long, long?)>();
        using var q = db.CreateCommand();
        q.CommandText = "SELECT id,json,updated_at,deleted_at FROM todos WHERE version>@s ORDER BY version";
        q.Parameters.AddWithValue("@s", since);
        using var r = q.ExecuteReader();
        while (r.Read())
            list.Add((r.GetString(0), r.GetString(1), r.GetInt64(2),
                r.IsDBNull(3) ? null : r.GetInt64(3)));
        return list;
    }

    public static bool TouchDevice(SqliteConnection db, string hash, long now)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE devices SET last_seen=@n WHERE token_hash=@h";
        cmd.Parameters.AddWithValue("@n", now);
        cmd.Parameters.AddWithValue("@h", hash);
        return cmd.ExecuteNonQuery() == 1;
    }

    public static void InsertDevice(SqliteConnection db, string hash, string name, long now)
        => Exec(db, "INSERT OR REPLACE INTO devices(token_hash,name,paired_at,last_seen) VALUES(@h,@n,@p,@p)",
            ("@h", hash), ("@n", name), ("@p", now));

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
}
