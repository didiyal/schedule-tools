// TodoSync —— 待办计时器同步枢纽（v1.5 S1）
// 职责：多端（桌面 WPF / 手机 APP）之间的任务数据合并与分发。
// 设计要点：
//   · 单用户模型：桌面端持管理员令牌（data/admin.token，首启生成），手机经配对码换设备令牌。
//   · 合并：按条目 updatedAt 秒级 LWW；删除走墓碑（deleted_at），服务端永久保留墓碑行。
//   · 条目正文以 JSON 原文存储（不解析字段），契约演进零迁移。
//   · 全局递增 version（meta 表），每次接受变更批次 +1；拉增量 = WHERE version > since。
//   · 部署形态：Kestrel 只听 127.0.0.1:5000，TLS 由宿主 Nginx(443) 终结（自签+客户端钉证书）。
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

// ── 启动参数与数据目录 ──
string? dataDirArg = null;
for (var i = 0; i < args.Length - 1; i++)
    if (args[i] == "--data-dir") dataDirArg = args[i + 1];
var dataDir = dataDirArg
    ?? Environment.GetEnvironmentVariable("TODOSYNC_DATA")
    ?? Path.Combine(AppContext.BaseDirectory, "data");
Directory.CreateDirectory(dataDir);
var dbPath = Path.Combine(dataDir, "todos.db");
var adminTokenPath = Path.Combine(dataDir, "admin.token");

if (!File.Exists(adminTokenPath))
{
    File.WriteAllText(adminTokenPath, NewToken() + Environment.NewLine);
    Console.WriteLine($"[TodoSync] 管理员令牌已生成: {adminTokenPath}（桌面端设置里粘贴一次）");
}
var adminHash = Sha256Hex(File.ReadAllText(adminTokenPath).Trim());

// ── SQLite 初始化 ──
using (var db = OpenDb(dbPath))
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

var Gate = new object();   // 单用户低并发，全局写锁即可，SQLite WAL 只防意外

var builder = WebApplication.CreateBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
var app = builder.Build();

// ── 鉴权 ──
static string? Bearer(HttpRequest req)
{
    var h = req.Headers.Authorization.ToString();
    return h.StartsWith("Bearer ", StringComparison.Ordinal) ? h["Bearer ".Length..].Trim() : null;
}

bool IsAdmin(string? token) => token is not null && FixedEq(Sha256Hex(token), adminHash);

// 设备令牌命中则刷新 last_seen 并放行
bool TouchDevice(string? token)
{
    if (token is null || token.Length < 16) return false;
    var hash = Sha256Hex(token);
    using var db = OpenDb(dbPath);
    using var cmd = db.CreateCommand();
    cmd.CommandText = "UPDATE devices SET last_seen=@n WHERE token_hash=@h";
    cmd.Parameters.AddWithValue("@n", Now());
    cmd.Parameters.AddWithValue("@h", hash);
    return cmd.ExecuteNonQuery() == 1;
}

IResult? Auth(HttpContext ctx)
{
    var t = Bearer(ctx.Request);
    if (IsAdmin(t)) return null;
    if (TouchDevice(t)) return null;
    return Results.Json(new { error = "unauthorized" }, statusCode: 401);
}

// ── 健康检查（免鉴权，无敏感信息）──
app.MapGet("/healthz", () =>
{
    lock (Gate)
    {
        using var db = OpenDb(dbPath);
        return Results.Json(new { ok = true, version = GetVersion(db) });
    }
});

// ── 配对：配对码（桌面端管理员生成，5 分钟单次有效）→ 设备令牌 ──
app.MapPost("/api/pair", (PairReq req) =>
{
    lock (Gate)
    {
        using var db = OpenDb(dbPath);
        var (code, exp) = GetPair(db);
        if (code is null || Now() > exp || req.Code is null || !FixedEq(req.Code.Trim(), code))
            return Results.Json(new { error = "invalid-or-expired-code" }, statusCode: 403);
        var token = NewToken();
        Exec(db, "INSERT OR REPLACE INTO devices(token_hash,name,paired_at,last_seen) VALUES(@h,@n,@p,@p)",
            ("@h", Sha256Hex(token)), ("@n", req.Name ?? ""), ("@p", Now()));
        Exec(db, "DELETE FROM meta WHERE k IN ('pair_code','pair_expiry')");
        return Results.Json(new { token });
    }
});

// ── 上行变更：逐条 LWW（只接受更新的），批次版本 +1 ──
app.MapPost("/api/changes", (HttpContext ctx, ChangesReq? req) =>
{
    var deny = Auth(ctx);
    if (deny is not null) return deny;
    if (req?.Items is null || req.Items.Length == 0)
        return Results.Json(new { error = "empty-items" }, statusCode: 400);
    lock (Gate)
    {
        using var db = OpenDb(dbPath);
        using var tx = db.BeginTransaction();
        var ver = GetVersion(db) + 1;
        var accepted = 0;
        foreach (var it in req.Items)
        {
            if (string.IsNullOrEmpty(it.Id) || it.Json.ValueKind != JsonValueKind.Object) continue;
            using (var q = db.CreateCommand())
            {
                q.CommandText = "SELECT updated_at FROM todos WHERE id=@id";
                q.Parameters.AddWithValue("@id", it.Id);
                var cur = q.ExecuteScalar();
                if (cur is not null && Convert.ToInt64(cur) >= it.UpdatedAt) continue;   // LWW：旧的丢弃
            }
            Exec(db, """
                INSERT INTO todos(id,json,updated_at,deleted_at,version) VALUES(@id,@j,@u,@d,@v)
                ON CONFLICT(id) DO UPDATE SET json=@j, updated_at=@u, deleted_at=@d, version=@v
                """,
                ("@id", it.Id), ("@j", it.Json.GetRawText()), ("@u", it.UpdatedAt),
                ("@d", it.DeletedAt), ("@v", ver));
            accepted++;
        }
        if (accepted > 0)
            Exec(db, "INSERT INTO meta(k,v) VALUES('version',@v) ON CONFLICT(k) DO UPDATE SET v=@v", ("@v", ver));
        tx.Commit();
        return Results.Json(new { version = GetVersion(db), accepted });
    }
});

// ── 拉增量 / 全量（since=0 即全量快照，含墓碑）──
app.MapGet("/api/changes", (HttpContext ctx, long since) =>
{
    var deny = Auth(ctx);
    if (deny is not null) return deny;
    lock (Gate)
    {
        using var db = OpenDb(dbPath);
        var items = new List<object>();
        using (var q = db.CreateCommand())
        {
            q.CommandText = "SELECT id,json,updated_at,deleted_at FROM todos WHERE version>@s ORDER BY version";
            q.Parameters.AddWithValue("@s", since);
            using var r = q.ExecuteReader();
            while (r.Read())
                items.Add(new ItemDto(r.GetString(0), r.GetInt64(2),
                    r.IsDBNull(3) ? null : r.GetInt64(3),
                    JsonDocument.Parse(r.GetString(1)).RootElement.Clone()));
        }
        return Results.Json(new { version = GetVersion(db), items });
    }
});

// ── 管理员：生成配对码 / 设备管理 ──
app.MapPost("/api/admin/paircode", (HttpContext ctx) =>
{
    if (!IsAdmin(Bearer(ctx.Request)))
        return Results.Json(new { error = "unauthorized" }, statusCode: 401);
    lock (Gate)
    {
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        var exp = Now() + 300;
        using var db = OpenDb(dbPath);
        Exec(db, "INSERT INTO meta(k,v) VALUES('pair_code',@c) ON CONFLICT(k) DO UPDATE SET v=@c", ("@c", code));
        Exec(db, "INSERT INTO meta(k,v) VALUES('pair_expiry',@e) ON CONFLICT(k) DO UPDATE SET v=@e", ("@e", exp));
        return Results.Json(new { code, expiresAt = exp });
    }
});

app.MapGet("/api/admin/devices", (HttpContext ctx) =>
{
    if (!IsAdmin(Bearer(ctx.Request)))
        return Results.Json(new { error = "unauthorized" }, statusCode: 401);
    lock (Gate)
    {
        using var db = OpenDb(dbPath);
        var list = new List<DeviceDto>();
        using (var q = db.CreateCommand())
        {
            q.CommandText = "SELECT token_hash,name,paired_at,last_seen FROM devices ORDER BY paired_at";
            using var r = q.ExecuteReader();
            while (r.Read())
                list.Add(new DeviceDto(r.GetString(0)[..12] + "…", r.GetString(1),
                    r.GetInt64(2), r.GetInt64(3)));
        }
        return Results.Json(new { devices = list });
    }
});

app.MapDelete("/api/admin/devices/{hash}", (HttpContext ctx, string hash) =>
{
    if (!IsAdmin(Bearer(ctx.Request)))
        return Results.Json(new { error = "unauthorized" }, statusCode: 401);
    lock (Gate)
    {
        using var db = OpenDb(dbPath);
        Exec(db, "DELETE FROM devices WHERE token_hash=@h", ("@h", hash));
        return Results.Json(new { ok = true });
    }
});

app.Run();
return;

// ── 本地函数 ──
static SqliteConnection OpenDb(string path)
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

static void Exec(SqliteConnection db, string sql, params (string Name, object? Value)[] ps)
{
    using var cmd = db.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
    cmd.ExecuteNonQuery();
}

static long GetVersion(SqliteConnection db)
{
    using var q = db.CreateCommand();
    q.CommandText = "SELECT v FROM meta WHERE k='version'";
    return q.ExecuteScalar() is string s ? long.Parse(s) : 0;
}

static (string? Code, long Exp) GetPair(SqliteConnection db)
{
    using var q = db.CreateCommand();
    q.CommandText = "SELECT k,v FROM meta WHERE k IN ('pair_code','pair_expiry')";
    string? code = null; long exp = 0;
    using var r = q.ExecuteReader();
    while (r.Read())
        if (r.GetString(0) == "pair_code") code = r.GetString(1);
        else exp = long.Parse(r.GetString(1));
    return (code, exp);
}

static string NewToken()
{
    var b = RandomNumberGenerator.GetBytes(32);
    return "tds_" + Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

static string Sha256Hex(string s)
    => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

static bool FixedEq(string a, string b)
{
    var ba = Encoding.UTF8.GetBytes(a);
    var bb = Encoding.UTF8.GetBytes(b);
    return ba.Length == bb.Length && CryptographicOperations.FixedTimeEquals(ba, bb);
}

static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

// ── 请求/响应 DTO（类型声明必须在全部顶级语句之后）──
record ItemDto(string Id, long UpdatedAt, long? DeletedAt, JsonElement Json);
record ChangesReq(ItemDto[]? Items);
record PairReq(string? Code, string? Name);
record DeviceDto(string Hash, string Name, long PairedAt, long LastSeen);
