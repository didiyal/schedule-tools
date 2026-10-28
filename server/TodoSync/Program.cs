// TodoSync —— 待办计时器同步枢纽（v1.5 S1）
// 职责：多端（桌面 WPF / 手机 APP）之间的任务数据合并与分发。
// 设计要点：
//   · 单用户模型：桌面端持管理员令牌（data/admin.token，首启生成），手机经配对码换设备令牌。
//   · 合并：按条目 updatedAt 秒级 LWW；删除走墓碑（deleted_at），服务端永久保留墓碑行。
//   · 条目正文以 JSON 原文存储（不解析字段），契约演进零迁移。
//   · 全局递增 version（meta 表），每次接受变更批次 +1；拉增量 = WHERE version > since。
//   · 部署形态：Kestrel 只听 127.0.0.1:5000，TLS 由宿主 Nginx(443) 终结（自签+客户端钉证书）。
//   · 数据访问在 Store.cs，通用工具在 Util.cs——本文件只有装配与 HTTP 路由。
using System.Net.WebSockets;
using System.Text;
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

// ── WebSocket 变更门铃（v1.4.7）──
// WS 只广播"有变化+版本号"，数据本体仍走 GET /api/changes 增量拉——合并逻辑零重写。
// 连接按空间分组；POST /api/changes 接受变更后向同空间在线端广播（fire-and-forget）。
var wsLock = new object();
var wsClients = new List<(string Space, WebSocket Socket)>();

void BroadcastChange(string space, long version)
{
    byte[] payload;
    try { payload = Encoding.UTF8.GetBytes("{\"type\":\"change\",\"version\":" + version + "}"); }
    catch { return; }
    List<System.Net.WebSockets.WebSocket> targets;
    lock (wsLock)
        targets = wsClients.Where(c => c.Space == space).Select(c => c.Socket).ToList();
    foreach (var sock in targets)
        _ = Task.Run(async () =>
        {
            try
            {
                if (sock.State == WebSocketState.Open)
                    await sock.SendAsync(new ArraySegment<byte>(payload),
                        WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch { /* 死连接由接收循环清理 */ }
        });
}

app.UseWebSockets();

// ── 鉴权 ──
static string? Bearer(HttpRequest req)
{
    var h = req.Headers.Authorization.ToString();
    return h.StartsWith("Bearer ", StringComparison.Ordinal) ? h["Bearer ".Length..].Trim() : null;
}

bool IsAdmin(string? token) => token is not null && Util.FixedEq(Util.Sha256Hex(token), adminHash);

// 设备令牌命中则刷新 last_seen 并解析所属空间；管理员令牌固定 default 空间
const string DefaultSpace = "default";

(string? Space, bool Ok) ResolveSpace(HttpContext ctx)
{
    var t = Bearer(ctx.Request);
    if (IsAdmin(t)) return (DefaultSpace, true);
    if (t is not null && t.Length >= 16)
    {
        using var db = Store.OpenDb(dbPath);
        var space = Store.TouchDeviceSpace(db, Util.Sha256Hex(t), Util.Now());
        if (space is not null) return (space, true);
    }
    return (null, false);
}

IResult? AuthSpace(HttpContext ctx, out string space)
{
    var (sp, ok) = ResolveSpace(ctx);
    space = sp ?? "";
    if (ok) { space = sp!; return null; }
    return Results.Json(new { error = "unauthorized" }, statusCode: 401);
}


// ── 健康检查（免鉴权，无敏感信息）──
app.MapGet("/healthz", () =>
{
    lock (gate)
    {
        using var db = Store.OpenDb(dbPath);
        return Results.Json(new { ok = true, version = Store.GetVersion(db), app = Util.AppVersion });
    }
});

// ── APP 更新包分发（免鉴权，APK 本就公开；路径可 env TODOSYNC_APK 覆盖）──
// 发布流程见 server/TodoSync/README.md「APP 更新包发布」：scp APK 到该路径即生效。
var apkPath = Environment.GetEnvironmentVariable("TODOSYNC_APK")
    ?? Path.Combine(dataDir, "apk", "todo-timer.apk");
app.MapGet("/api/app/latest", () =>
{
    if (!File.Exists(apkPath))
        return Results.Json(new { error = "no-apk-uploaded" }, statusCode: 404);
    return Results.File(apkPath, "application/vnd.android.package-archive",
        fileDownloadName: $"TodoTimer-{Util.AppVersion}.apk");
});

// ── 桌面端安装包分发（免鉴权，同 APK 模式；env TODOSYNC_DESKTOP_PKG 可覆盖）──
// 桌面端更新横幅点「下载」即拉此文件，静默安装后自动重启。
var desktopPkgPath = Environment.GetEnvironmentVariable("TODOSYNC_DESKTOP_PKG")
    ?? Path.Combine(dataDir, "desktop", "TodoTimer-Setup.exe");
app.MapGet("/api/app/desktop", () =>
{
    if (!File.Exists(desktopPkgPath))
        return Results.Json(new { error = "no-desktop-package" }, statusCode: 404);
    return Results.File(desktopPkgPath, "application/octet-stream",
        fileDownloadName: $"TodoTimer_Setup-{Util.AppVersion}.exe");
});

// WS 入口：鉴权同 REST（Bearer 头）；客户端每 25s 发 ping 保活（nginx read_timeout 60s）
app.Map("/api/ws", async (HttpContext ctx) =>
{
    string? space;
    var t = Bearer(ctx.Request);
    if (IsAdmin(t)) space = DefaultSpace;
    else if (t is not null && t.Length >= 16)
    {
        using var db = Store.OpenDb(dbPath);
        space = Store.TouchDeviceSpace(db, Util.Sha256Hex(t), Util.Now());
    }
    else space = null;
    if (space is null) { ctx.Response.StatusCode = 401; return; }
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }

    var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    lock (wsLock) wsClients.Add((space, ws));
    var buf = new byte[4096];
    try
    {
        while (ws.State == WebSocketState.Open)
        {
            var res = await ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
            if (res.MessageType == WebSocketMessageType.Close)
            {
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                break;
            }
            // 客户端 ping 帧直接忽略（收包本身即保活）
        }
    }
    catch { }
    finally
    {
        lock (wsLock) wsClients.RemoveAll(c => c.Socket == ws);
    }
});

// ── 配对：配对码（桌面端管理员生成，5 分钟单次有效）→ 设备令牌 ──
app.MapPost("/api/pair", (PairReq req) =>
{
    lock (gate)
    {
        using var db = Store.OpenDb(dbPath);
        var (code, exp, pairSpace) = Store.GetPairSpace(db);
        if (code is null || Util.Now() > exp || req.Code is null
            || !Util.FixedEq(req.Code.Trim(), code))
            return Results.Json(new { error = "invalid-or-expired-code" }, statusCode: 403);
        var token = Util.NewToken();
        Store.InsertDevice(db, Util.Sha256Hex(token), req.Name ?? "", pairSpace, Util.Now());
        Store.ClearPair(db);
        return Results.Json(new { token, space = pairSpace });
    }
});

// ── 上行变更：逐条 LWW（只接受更新的），批次版本 +1 ──
app.MapPost("/api/changes", (HttpContext ctx, ChangesReq? req) =>
{
    var deny = AuthSpace(ctx, out var space);
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
            Store.UpsertTodo(db, space, it.Id,
                it.Json.ValueKind == JsonValueKind.Object ? it.Json.GetRawText() : "{}",
                it.UpdatedAt, it.DeletedAt, ver);
            accepted++;
        }
        if (accepted > 0)
        {
            Store.SetVersion(db, ver);
            tx.Commit();
            BroadcastChange(space, ver);   // 同空间在线端秒级收到变更门铃
        }
        return Results.Json(new { version = Store.GetVersion(db), accepted });
    }
});

// ── 拉增量 / 全量（since=0 即全量快照，含墓碑）──
app.MapGet("/api/changes", (HttpContext ctx, long since) =>
{
    var deny = AuthSpace(ctx, out var space);
    if (deny is not null) return deny;
    lock (gate)
    {
        using var db = Store.OpenDb(dbPath);
        var items = Store.ChangesSince(db, space, since)
            .Select(r => new ItemDto(r.Id, r.UpdatedAt, r.DeletedAt,
                JsonDocument.Parse(r.Json).RootElement.Clone()))
            .ToList();
        return Results.Json(new { version = Store.GetVersion(db), items, app = Util.AppVersion });
    }
});

// ── 管理员：生成配对码 / 设备管理 ──
app.MapPost("/api/admin/paircode", (HttpContext ctx, PairCodeReq? req) =>
{
    if (!IsAdmin(Bearer(ctx.Request)))
        return Results.Json(new { error = "unauthorized" }, statusCode: 401);
    // 空间标签白名单：小写字母/数字/下划线/连字符，≤32
    var space = req?.Space is { Length: > 0 } sp ? sp.Trim() : DefaultSpace;
    if (space.Length > 32 || !space.All(char.IsAsciiLetterOrDigit) || space != space.ToLowerInvariant())
        return Results.Json(new { error = "invalid-space" }, statusCode: 400);
    lock (gate)
    {
        var code = Random.Shared.Next(0, 1_000_000).ToString("D6");
        var exp = Util.Now() + 300;
        using var db = Store.OpenDb(dbPath);
        Store.SetPair(db, code, exp, space);
        return Results.Json(new { code, expiresAt = exp, space });
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

// 运维：空间清单（空间/条目数，含墓碑）
app.MapGet("/api/admin/spaces", (HttpContext ctx) =>
{
    if (!IsAdmin(Bearer(ctx.Request)))
        return Results.Json(new { error = "unauthorized" }, statusCode: 401);
    lock (gate)
    {
        using var db = Store.OpenDb(dbPath);
        var spaces = Store.SpaceStats(db)
            .Select(s => new { space = s.Space, todos = s.Count })
            .ToList();
        return Results.Json(new { spaces });
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
record PairCodeReq(string? Space);
