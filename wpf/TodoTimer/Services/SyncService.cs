using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Net.Security;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using TodoTimer.Models;

namespace TodoTimer.Services;

/// <summary>同步配置与状态，持久化在 data/syncstate.json。</summary>
public sealed class SyncState
{
    public string Server { get; set; } = "";        // 如 https://47.112.114.139
    public string AdminToken { get; set; } = "";    // 管理员令牌（桌面端=权威端）
    public long ServerVersion { get; set; }         // 已拉到的服务端版本
    public long PushedAtHighWater { get; set; }     // 已上行条目的 UpdatedAt 高水位
    public bool Seeded { get; set; }                // 存量已补推（首次配置后首轮全量上行）
    public string? PinnedFingerprint { get; set; }  // 服务器证书 SHA-256（hex 冒号），设置时锁定
    public Dictionary<string, string> Fingerprints { get; set; } = new();   // id → 内容指纹
    public Dictionary<string, long> Tombstones { get; set; } = new();       // id → 删除时刻
    public bool Enabled => Server.Length > 0 && AdminToken.Length > 0;
}

/// <summary>
/// 桌面端同步服务（v1.5 S2）：
///   推：存盘钩子 OnSaved → 指纹 diff（SyncOps.BumpChanged 置 UpdatedAt）→ 上行 >高水位 的条目+墓碑；
///   拉：App 30s tick 调 PollNow → 下行增量 → SyncOps.ApplyToList 合并 → 回调重建。
/// 单用户权威端：本地改 = 本地胜（UpdatedAt=now），远端改仅在更新时覆盖。
/// 失败静默记状态字符串（状态栏可见），绝不阻塞/破坏本地功能。
/// </summary>
public sealed class SyncService : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _statePath;
    private SyncState _state = new();
    private readonly object _gate = new();      // _state/_busy 串行化
    private bool _busy;
    private bool _updateHintShown;

    /// <summary>合并落库后回调（VM：Rebuild+Save）。在 Poll 的调用线程触发。</summary>
    public event Action? Merged;
    public event Action<string>? StatusChanged;
    public string Status { get; private set; } = "";

    public SyncService(string dataDir)
    {
        _statePath = Path.Combine(dataDir, "syncstate.json");
        LoadState();
        _http = new HttpClient(new HttpClientHandler
        {
            // 自签证书放行由下面的指纹校验兜底：PinnedFingerprint 非空时逐请求比对
            ServerCertificateCustomValidationCallback = ValidatePin,
        })
        { Timeout = TimeSpan.FromSeconds(15) };
    }

    private bool ValidatePin(HttpRequestMessage req, X509Certificate2? cert,
        X509Chain? chain, SslPolicyErrors errors)
    {
        var pin = _state.PinnedFingerprint;
        if (string.IsNullOrEmpty(pin)) return true;   // 未钉：放行（设置对话框首次连接时钉上）
        if (cert is null) return false;
        var fp = Convert.ToHexString(cert.GetCertHash()) is { } raw
            ? string.Join(':', Enumerable.Range(0, raw.Length / 2)
                .Select(i => raw.Substring(i * 2, 2)))
            : "";
        return string.Equals(fp, pin, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>装载本地文件后初始化指纹表：全量登记当前指纹且不置 UpdatedAt（不上行存量数据）。</summary>
    public void InitFingerprints(List<TodoItem> todos)
    {
        lock (_gate)
        {
            if (_state.Fingerprints.Count > 0) return;   // 已有状态必须保留——重置会把远端改动当本地新改
            foreach (var t in todos) _state.Fingerprints[t.Id] = SyncOps.Fingerprint(t);
            SaveState();
        }
    }

    /// <summary>保存服务器配置。连接成功时钉证书指纹（自签+钉证书方案）。</summary>
    public async Task<string?> ConfigureAsync(string server, string adminToken)
    {
        server = server.Trim().TrimEnd('/');
        adminToken = adminToken.Trim();
        if (server.Length == 0 || adminToken.Length == 0) return "服务器与令牌不能为空";

        // 探测 + 钉证书：/healthz 免鉴权，顺带拿服务器版本
        long version;
        try
        {
            using var resp = await _http.GetAsync(server + "/healthz");
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            version = doc.RootElement.GetProperty("version").GetInt64();
        }
        catch (Exception ex)
        {
            return "连接失败：" + TrimErr(ex);
        }

        // 拿到响应即有证书：首次配置锁指纹（用户主动配置=信任此次连接），已钉则此连接必须匹配
        var fp = _lastCertFingerprint;
        if (_state.PinnedFingerprint is { } old && fp is not null
            && !string.Equals(old, fp, StringComparison.OrdinalIgnoreCase))
            return "证书指纹与已锁定值不符（可能中间人/服务器重装），拒绝保存";
        lock (_gate)
        {
            _state.Server = server;
            _state.AdminToken = adminToken;
            _state.ServerVersion = version;      // 从当前版本起增量拉
            _state.PinnedFingerprint ??= fp;
            // 新配置/换服务器 → 清指纹表（下轮对账把全部条目当首次登记）并标记未播种，
            // 首轮推送把存量全量灌上服务器（否则新设备只能拉到零散增量）
            if (_state.Seeded == false || _state.Fingerprints.Count == 0)
                _state.Seeded = false;
            SaveState();
        }
        SetStatus($"已连接，服务器版本 {version}");
        return null;   // null = 成功
    }

    /// <summary>最近一次 HTTPS 响应的证书指纹（ValidatePin 无法回传，用字段带出）。</summary>
    private string? _lastCertFingerprint;

    public void ClearConfig()
    {
        lock (_gate)
        {
            _state = new SyncState();
            SaveState();
        }
        SetStatus("同步已关闭");
    }

    public SyncState SnapshotState() { lock (_gate) return CloneState(_state); }

    private static SyncState CloneState(SyncState s) => new()
    {
        Server = s.Server,
        AdminToken = s.AdminToken,
        ServerVersion = s.ServerVersion,
        PushedAtHighWater = s.PushedAtHighWater,
        Seeded = s.Seeded,
        PinnedFingerprint = s.PinnedFingerprint,
        Fingerprints = new Dictionary<string, string>(s.Fingerprints),
        Tombstones = new Dictionary<string, long>(s.Tombstones),
    };

    private void LoadState()
    {
        try
        {
            if (File.Exists(_statePath))
                _state = JsonSerializer.Deserialize<SyncState>(File.ReadAllText(_statePath)) ?? new();
        }
        catch { _state = new SyncState(); }   // 坏文件按未配置处理，不阻断启动
    }

    private void SaveState()
    {
        try
        {
            var tmp = _statePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_state, TodoStore.WriteOpts), new UTF8Encoding(false));
            File.Move(tmp, _statePath, overwrite: true);
        }
        catch { /* 状态落盘失败不阻断同步 */ }
    }

    private void SetStatus(string s)
    {
        Status = s;
        StatusChanged?.Invoke(s);
    }

    /// <summary>
    /// 存盘钩子：触发一轮完整同步（后台，不阻塞 UI）。
    /// 必须走 PollNowAsync（先拉后推）而不是直接 PushCoreAsync——直接推会把
    /// 【尚未合并手机端改动】的本地状态推上去盖掉远端（2026-10-28 用户报根因之一）。
    /// _busy 守卫保证突发连存只有一轮在跑；最后一轮若赶不上末次保存，
    /// 改动仍会被指纹标记、由 30s 心跳兜底补推。
    /// </summary>
    public void OnSaved(Func<List<TodoItem>> getTodos)
        => Task.Run(async () =>
        {
            await PollNowAsync(getTodos);
        });

    /// <summary>
    /// 对账：指纹 diff 置 UpdatedAt + 本地消失条目墓碑化（返回本批待上行内容）。
    /// 消失检测不能只挂在 Save 钩子上——CLI/远端合并改列表不经过 Save，
    /// 由 Push 与 Poll 各自调用（Poll 是固定心跳，兜住所有外部变更路径）。
    /// 调用方持锁无必要：todos 为 vm 权威引用，与 UI 线程的竞态由“先快照后比对”化解。
    /// </summary>
    private List<(string Id, TodoItem? Item, long Ts, bool Deleted)> Reconcile(List<TodoItem> todos, long now)
    {
        var batch = new List<(string, TodoItem?, long, bool)>();
        lock (_gate)
        {
            foreach (var t in todos)
            {
                var fp = SyncOps.Fingerprint(t);
                var changed = !_state.Fingerprints.TryGetValue(t.Id, out var old) || old != fp;
                // 播种期（Seeded=false）：不看 changed，全部条目上行一次（存量补推）
                if (!_state.Seeded) changed = true;
                if (changed)
                {
                    // 播种期：全量上行（时间戳统一置 now，服务端 LWW 收编）；
                    // 播种后：已登记条目变更 → 置 now 上行；真·本地新建（UpdatedAt==0）→ 置 now 上行；
                    // 远端带下来的存量只补指纹不回推。
                    var known = _state.Fingerprints.ContainsKey(t.Id);
                    if (!_state.Seeded || known || t.UpdatedAt == 0)
                    {
                        t.UpdatedAt = now;
                        batch.Add((t.Id, t, now, false));
                    }
                    _state.Fingerprints[t.Id] = fp;
                }
            }
            // 本地已消失的条目 → 墓碑上行
            foreach (var id in _state.Fingerprints.Keys.Where(id => !todos.Any(t => t.Id == id)).ToList())
            {
                _state.Fingerprints.Remove(id);
                _state.Tombstones[id] = now;
                batch.Add((id, null, now, true));
            }
            // 墓碑重试：Tombstones 里有、但列表里也没有的 → 重发直到服务端确认
            // （历史教训：墓碑只在"指纹表有而列表无"时生成一次；若那次上传失败
            //  （如旧服务端拒收 json=null），墓碑永远不会再进批次 → 删除在服务端永不生效）
            foreach (var (id, ts) in _state.Tombstones.ToList())
            {
                if (!todos.Any(t => t.Id == id) && !batch.Any(b => b.Item1 == id))
                    batch.Add((id, null, ts, true));
            }
        }
        return batch;
    }

    private async Task PushCoreAsync(List<TodoItem> todos, bool quietWhenIdle = false)
    {
        try
        {
            lock (_gate)
            {
                if (!_state.Enabled) { Exit(); return; }
            }
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var raw = Reconcile(todos, now);
            Log($"push: todos={todos.Count} batch={raw.Count}(del={raw.Count(b => b.Deleted)}) now={now}");
            var batch = raw.Select(b =>
            {
                JsonElement json;
                if (b.Item is { } item) json = JsonSerializer.SerializeToElement(item);
                else json = JsonSerializer.Deserialize<JsonElement>("{\"id\":\"" + b.Id + "\"}");
                return new
                {
                    id = b.Id,
                    updatedAt = b.Ts,
                    deletedAt = b.Deleted ? (long?)b.Ts : null,
                    json,   // 墓碑也带最小合法对象（服务端要求批次条目 json 为对象才接受）
                };
            }).ToList();
            if (batch.Count > 0)
            {
                var json = JsonSerializer.Serialize(new { items = batch },
                    new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                using var req = new HttpRequestMessage(HttpMethod.Post, _state.Server + "/api/changes");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _state.AdminToken);
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                using var resp = await _http.SendAsync(req);
                if ((int)resp.StatusCode == 401) { SetStatus("同步失败：令牌被拒(401)"); return; }
                resp.EnsureSuccessStatusCode();
                lock (_gate)
                {
                    _state.PushedAtHighWater = Math.Max(_state.PushedAtHighWater, batch.Max(b => b.updatedAt));
                    _state.Seeded = true;            // 本批含存量补推完成，之后回归增量
                    // 墓碑送达即清账：服务端 200 = 收下（含 LWW 拒收——拒收说明服务端已有
                    // 相同或更新状态，都算确认）。不能等"拉取见到服务端墓碑"再清——
                    // 增量拉取只看 since=当前版本，老墓碑永远不再出现在增量里 → 永久重发。
                    foreach (var b in raw.Where(b => b.Deleted))
                        _state.Tombstones.Remove(b.Id);
                    SaveState();
                }
            }
            else
            {
                lock (_gate) SaveState();
                if (quietWhenIdle) return;   // 轮询里的空对账：不刷状态行
            }
            SetStatus($"已同步 {DateTime.Now:HH:mm:ss}");
        }
        catch (Exception ex)
        {
            SetStatus("同步失败：" + TrimErr(ex));
        }
    }

    /// <summary>定时拉：先对账（兜住 CLI/合并等不经 Save 的外部变更）再下行合并。App 30s tick 调用。</summary>
    public async Task PollNowAsync(Func<List<TodoItem>> getTodos)
    {
        if (!await EnterAsync()) return;
        try
        {
            long ver, since;
            lock (_gate)
            {
                if (!_state.Enabled) { Exit(); return; }
                ver = since = _state.ServerVersion;
            }
            // 先拉后推（2026-10-28 用户报「手机暂停后电脑端不更新」根因）：
            // 旧顺序=先推后拉——本地保存会立即把【未合并手机端改动】的陈旧状态推上服务器，
            // 把手机端刚写的暂停/勾选盖掉。改为先拉合并远端最新，再推本地净变更。
            // 行末的无条件 push 仍覆盖「CLI 删除、合并剔除等不触发 Save 的路径」对账。
            lock (_gate) since = _state.ServerVersion;
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"{_state.Server}/api/changes?since={since}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _state.AdminToken);
            using var resp = await _http.SendAsync(req);
            if ((int)resp.StatusCode == 401) { SetStatus("拉取失败：令牌被拒(401)"); return; }
            resp.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            ver = root.GetProperty("version").GetInt64();
            if (root.TryGetProperty("app", out var appEl))
            {
                var serverApp = appEl.GetString();
                if (!string.IsNullOrEmpty(serverApp) && IsNewerVersion(serverApp, Models.AppInfo.Version)
                    && !_updateHintShown)
                {
                    _updateHintShown = true;
                    SetStatus($"服务器有新版本 {serverApp}，建议更新桌面端");
                }
            }
            var todos = getTodos();
            Log($"poll: since={since} serverVer={ver} todos={todos.Count}");
            var changed = false;
            foreach (var it in root.GetProperty("items").EnumerateArray())
            {
                var id = it.GetProperty("id").GetString();
                if (string.IsNullOrEmpty(id)) continue;
                var ua = it.TryGetProperty("updatedAt", out var u) ? u.GetInt64() : 0;
                long? del = it.TryGetProperty("deletedAt", out var d) && d.ValueKind == JsonValueKind.Number
                    ? d.GetInt64() : null;
                var remote = new RemoteItem(id!, ua, del, it.GetProperty("json").Clone());
                if (SyncOps.ApplyToList(todos, remote)) changed = true;
                // 服务端已确认的墓碑 → 清本地重试项（否则永久重发）
                if (del is not null)
                    lock (_gate) _state.Tombstones.Remove(id!);
            }
            lock (_gate)
            {
                _state.ServerVersion = ver;
                SaveState();
            }
            if (changed) Merged?.Invoke();   // VM 重建 + Save（Save→OnSaved 推回流，LWW 防回卷）
            // 合并远端之后再推本地净变更（先拉后推的“推”）：保证推上去的状态已含手机端最新改动
            await PushCoreAsync(getTodos(), quietWhenIdle: true);
            SetStatus($"已同步 {DateTime.Now:HH:mm:ss}");
        }
        catch (Exception ex)
        {
            SetStatus("同步失败：" + TrimErr(ex));
        }
        finally { Exit(); }
    }

    private bool Enter()
    {
        lock (_gate)
        {
            if (_busy) return false;
            _busy = true;
            return true;
        }
    }

    /// <summary>服务器版本号是否比本地新（语义化比较；解析失败退回「不相等」）。</summary>
    internal static bool IsNewerVersion(string server, string local)
    {
        if (server == local) return false;
        if (Version.TryParse(server, out var s) && Version.TryParse(local, out var l))
            return s > l;
        return true;
    }

    private Task<bool> EnterAsync() => Task.FromResult(Enter());

    private void Exit() { lock (_gate) _busy = false; }

    private static string TrimErr(Exception ex)
        => ex.Message.Length > 60 ? ex.Message[..60] : ex.Message;

    /// <summary>诊断日志汇——产品库不耦合机器路径；组合根（App）按需接线，未接线=静默。</summary>
    public static Action<string>? LogSink;
    private static void Log(string msg) => LogSink?.Invoke($"{DateTime.Now:HH:mm:ss} {msg}");

    public void Dispose() => _http.Dispose();
}