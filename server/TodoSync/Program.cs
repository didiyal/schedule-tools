// TodoSync —— 待办计时器同步枢纽（v1.5 S1）
// 职责：多端（桌面 WPF / 手机 APP）之间的任务数据合并与分发。
// 设计要点：
//   · 单用户模型：桌面端持管理员令牌（data/admin.token，首启生成），手机经配对码换设备令牌。
//   · 合并：按条目 updatedAt 秒级 LWW；删除走墓碑（deleted_at），服务端永久保留墓碑行。
//   · 条目正文以 JSON 原文存储（不解析字段），契约演进零迁移。
//   · 全局递增 version（meta 表），每次接受变更批次 +1；拉增量 = WHERE version > since。
//   · 部署形态：Kestrel 只听 127.0.0.1:5000，TLS 由宿主 Nginx(443) 终结（自签+客户端钉证书）。
//   · 数据访问在 Store.cs，通用工具在 Util.cs——本文件只有装配与 HTTP 路由。
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TodoSync;

var dataDir = ReadArg(args, "--data-dir")
    ?? Environment.GetEnvironmentVariable("TODOSYNC_DATA")
    ?? Path.Combine(AppContext.BaseDirectory, "data");
Directory.CreateDirectory(dataDir);
var dbPath = Path.Combine(dataDir, "todos.db");
var adminTokenPath = Path.Combine(dataDir, "admin.token");

if (!File.Exists(adminTokenPath))
{
    File.WriteAllText(adminTokenPath, Util.NewToken() + Environment.NewLine);
    Console.WriteLine($"[TodoSync] 管理员令牌已生成: {adminTokenPath}（桌面端设置里粘贴一次）");
}
var adminHash = Util.Sha256Hex(File.ReadAllText(adminTokenPath).Trim());

using (var db = Store.OpenDb(dbPath)) Store.Init(db);

var gate = new object();   // 单用户低并发，全局写锁即可，SQLite WAL 只防意外

var builder = WebApplication.CreateBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
var app = builder.Build();

// ── 鉴权 ──
static string? Bearer(HttpRequest req)
{
    var h = req.Headers.Authorization.ToString();
    return h.StartsWith("Bearer ", StringComparison.Ordinal) ? h["Bearer ".Length..].Trim() : null;
}

bool IsAdmin(string? token) => token is not null && Util.FixedEq(Util.Sha256Hex(token), adminHash);

// 设备令牌命中则刷新 last_seen 并放行
bool TouchDevice(string? token)
{
    if (token is null || token.Length < 16) return false;
    using var db = Store.OpenDb(dbPath);
    return Store.TouchDevice(db, Util.Sha256Hex(token), Util.Now());
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
    lock (gate)
    {
        using var db = Store.OpenDb(dbPath);
        return Results.Json(new { ok = true, version = Store.GetVersion(db) });
    }
});

// ── 配对：配对码（桌面端管理员生成，5 分钟单次有效）→ 设备令牌 ──
app.MapPost("/api/pair", (PairReq req) =>
{
    lock (gate)
    {
        using var db = Store.OpenDb(dbPath);
        var (code, exp) = Store.GetPair(db);
        if (code is null || Util.Now() > exp || req.Code is null
            || !Util.FixedEq(req.Code.Trim(), code))
            return Results.Json(new { error = "invalid-or-expired-code" }, statusCode: 403);
        var token = Util.NewToken();
        Store.InsertDevice(db, Util.Sha256Hex(token), req.Name ?? "", Util.Now());
        Store.ClearPair(db);
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
    lock (gate)
    {
        using var db = Store.OpenDb(dbPath);
        using var tx = db.BeginTransaction();
        var ver = Store.GetVersion(db) + 1;
        var accepted = 0;
        foreach (var it in req.Items)
        {
            if (string.IsNullOrEmpty(it.Id)) continue;
            // 墓碑（deletedAt 非空）允许 json 缺失/非对象——删除只关心 id 与时间
            if (it.DeletedAt is null && it.Json.ValueKind != JsonValueKind.Object) continue;
            var cur = Store.TodoUpdatedAt(db, it.Id);
            if (cur is not null && cur >= it.UpdatedAt) continue;   // LWW：旧的丢弃
            Store.UpsertTodo(db, it.Id,
                it.Json.ValueKind == JsonValueKind.Object ? it.Json.GetRawText() : "{}",
                it.UpdatedAt, it.DeletedAt, ver);
            accepted++;
        }
        if (accepted > 0)
        {
            Store.SetVersion(db, ver);
            tx.Commit();
        }
        return Results.Json(new { version = Store.GetVersion(db), accepted });
    }
});

// ── 拉增量 / 全量（since=0 即全量快照，含墓碑）──
app.MapGet("/api/changes", (HttpContext ctx, long since) =>
{
    var deny = Auth(ctx);
    if (deny is not null) return deny;
    lock (gate)
    {
        using var db = Store.OpenDb(dbPath);
        var items = Store.ChangesSince(db, since)
            .Select(r => new ItemDto(r.Id, r.UpdatedAt, r.DeletedAt,
                JsonDocument.Parse(r.Json).RootElement.Clone()))
            .ToList();
        return Results.Json(new { version = Store.GetVersion(db), items });
    }
});

// ── 管理员：生成配对码 / 设备管理 ──
app.MapPost("/api/admin/paircode", (HttpContext ctx) =>
{
    if (!IsAdmin(Bearer(ctx.Request)))
        return Results.Json(new { error = "unauthorized" }, statusCode: 401);
    lock (gate)
    {
        var code = Random.Shared.Next(0, 1_000_000).ToString("D6");
        var exp = Util.Now() + 300;
        using var db = Store.OpenDb(dbPath);
        Store.SetPair(db, code, exp);
        return Results.Json(new { code, expiresAt = exp });
    }
});

app.MapGet("/api/admin/devices", (HttpContext ctx) =>
{
    if (!IsAdmin(Bearer(ctx.Request)))
        return Results.Json(new { error = "unauthorized" }, statusCode: 401);
    lock (gate)
    {
        using var db = Store.OpenDb(dbPath);
        var devices = Store.Devices(db)
            .Select(d => new DeviceDto(d.Hash[..12] + "…", d.Name, d.PairedAt, d.LastSeen))
            .ToList();
        return Results.Json(new { devices });
    }
});

app.MapDelete("/api/admin/devices/{hash}", (HttpContext ctx, string hash) =>
{
    if (!IsAdmin(Bearer(ctx.Request)))
        return Results.Json(new { error = "unauthorized" }, statusCode: 401);
    lock (gate)
    {
        using var db = Store.OpenDb(dbPath);
        Store.DeleteDevice(db, hash);
        return Results.Json(new { ok = true });
    }
});

app.Run();
return;

// ── 启动参数 ──
static string? ReadArg(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
        if (args[i] == name) return args[i + 1];
    return null;
}

// ── 请求/响应 DTO（类型声明必须在全部顶级语句之后）──
record ItemDto(string Id, long UpdatedAt, long? DeletedAt, JsonElement Json);
record ChangesReq(ItemDto[]? Items);
record PairReq(string? Code, string? Name);
record DeviceDto(string Hash, string Name, long PairedAt, long LastSeen);
