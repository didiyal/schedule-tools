using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using TodoTimer.Models;

namespace TodoTimer.Services;

/// <summary>
/// cli 子命令（headless 直读写 data/todos.json, 语义对照 Python todo_cli/cli_headless）。
/// 三态规则: GUI 在线时写命令拒绝（防覆盖, IPC 转发待下一片）, 读命令允许。
/// </summary>
public static class CliRunner
{
    private static string? _dataDir;
    private static bool _guiOnline;
    private static bool _json;

    private static string SavePath => Path.Combine(_dataDir!, "todos.json");
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static int Run(string[] args, string? dataDir = null)
    {
        _dataDir = dataDir ?? ViewModels.MainViewModel.ResolveDataDir();
        try { _guiOnline = Mutex.OpenExisting(@"Local\TodoTimer.WPF") is not null; }
        catch { _guiOnline = false; }

        if (args.Length == 0)
        {
            Err("用法: cli <list|add|done|undone|delete|start|pause|move|rename|star|reparent|search|stats> [参数] [--json]");
            return 2;
        }
        var cmd = args[0].ToLowerInvariant();

        // 参数解析: --开关 / --带值开关 值 / 位置参数
        var flags = new HashSet<string>();
        var values = new Dictionary<string, string>();
        var pos = new List<string>();
        for (var i = 1; i < args.Length; i++)
        {
            var a = args[i];
            if (!a.StartsWith("--")) { pos.Add(a); continue; }
            var name = a[2..];
            if (name is "date" or "parent")
            {
                if (i + 1 >= args.Length) { Err($"--{name} 缺少值"); return 2; }
                values[name] = args[++i];
            }
            else flags.Add(name);
        }
        _json = flags.Remove("json");
        flags.Remove("tree");       // 树形缩进暂以 Brief 的 parent/depth 呈现
        flags.Remove("all");

        try
        {
            return cmd switch
            {
                "list" => CmdList(pos, values, flags, search: null),
                "search" => CmdList(pos, values, flags, search: string.Join(' ', pos).ToLowerInvariant()),
                "add" => CmdAdd(pos, values, flags),
                "done" => CmdToggleDone(pos, true),
                "undone" => CmdToggleDone(pos, false),
                "delete" => CmdDelete(pos),
                "start" => CmdStartPause(pos, true),
                "pause" => CmdStartPause(pos, false),
                "move" => CmdMove(pos),
                "rename" => CmdRename(pos),
                "star" => CmdStar(pos, flags),
                "reparent" => CmdReparent(pos, values, flags),
                "stats" => CmdStats(),
                _ => Err($"未知命令: {cmd}"),
            };
        }
        catch (Exception ex)
        {
            Err("错误: " + ex.Message);
            return 1;
        }
    }

    // ── 数据访问 ──

    private static TodoFile Load()
    {
        var file = TodoStore.Load(SavePath) ?? new TodoFile();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var t in file.Todos)
        {
            if (t.Completed) t.Running = false;
            if (t.Running && t.LastStartedAt is not null)
            {
                t.Elapsed += (int)Math.Max(0, now - t.LastStartedAt.Value);
                t.LastStartedAt = (int)now;
            }
        }
        return file;
    }

    /// <summary>写命令落盘后调用：GUI 在线则通知其重载数据（IPC），不可达则提示。</summary>
    private static void NotifyGui()
    {
        if (!_guiOnline) return;
        try
        {
            IpcServer.NotifyReload();
        }
        catch (InvalidOperationException)
        {
            Err("警告: 改动已写入文件, 但 GUI 未响应刷新(可能已退出)。若界面未更新请重开 GUI。");
        }
    }

    private static Dictionary<string, TodoItem> Index(TodoFile f)
    {
        var d = new Dictionary<string, TodoItem>();
        foreach (var t in f.Todos) d[t.Id] = t;
        return d;
    }

    private static void Save(TodoFile f) => TodoStore.Save(SavePath, f);

    private static IEnumerable<string> Ids(List<string> pos)
        => pos.SelectMany(p => p.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    // ── 命令 ──

    private static int CmdList(List<string> pos, Dictionary<string, string> values,
        HashSet<string> flags, string? search)
    {
        var f = Load();
        var byId = Index(f);
        var parents = byId.ToDictionary(kv => kv.Key, kv => kv.Value.Parent);
        var today = TodoItem.TodayStr();
        var todayOnly = flags.Contains("today");

        IEnumerable<TodoItem> q = f.Todos;
        if (search is not null)
            q = q.Where(t => t.Text.ToLowerInvariant().Contains(search));
        else if (values.TryGetValue("date", out var d))
            q = q.Where(t => t.Date == d);
        if (todayOnly) q = q.Where(t => t.Date == today);

        var list = q.OrderByDescending(t => t.Date).ThenBy(t => t.Order).ToList();
        if (_json)
        {
            Out(JsonSerializer.Serialize(list.Select(t => new
            {
                id = t.Id,
                text = t.Text,
                date = t.Date,
                done = t.Completed,
                running = t.Running,
                starred = t.Starred,
                elapsed = t.Elapsed,
                parent = t.Parent,
                depth = TaskTree.DepthOf(parents, t.Id),
            }), JsonOpts));
            return 0;
        }
        foreach (var t in list) Out(Brief(t, parents));
        return 0;
    }

    private static string Brief(TodoItem t, IReadOnlyDictionary<string, string?> parents)
    {
        var mark = (t.Starred ? "★" : "") + (t.Completed ? "✓" : "") + (t.Running ? "▶" : "");
        var parts = $"#{t.Id} {(mark.Length > 0 ? $"[{mark}] " : "")}{t.Text}";
        if (t.Elapsed > 0 || t.Running) parts += $" ({Fmt(t.Elapsed)})";
        if (t.Date != TodoItem.TodayStr()) parts += $" ->{t.Date}";
        var pid = parents.TryGetValue(t.Id, out var p) ? p : null;
        if (pid is not null) parts += $" parent=#{pid} depth={TaskTree.DepthOf(parents, t.Id)}";
        return parts;
    }

    private static string Fmt(int sec)
    {
        var h = sec / 3600; var m = sec % 3600 / 60; var ss = sec % 60;
        return h > 0 ? $"{h}h{m:D2}m" : m > 0 ? $"{m}m{ss:D2}s" : $"{ss}s";
    }

    private static int CmdAdd(List<string> pos, Dictionary<string, string> values, HashSet<string> flags)
    {
        var text = string.Join(' ', pos);
        if (string.IsNullOrWhiteSpace(text))
        {
            Err("用法: cli add \"任务文本\" [--date yyyy-mm-dd] [--star] [--parent 父id]");
            return 2;
        }
        var f = Load();
        var byId = Index(f);
        string? parent = values.TryGetValue("parent", out var p) ? p : null;
        if (parent is not null && !byId.ContainsKey(parent)) { Err($"父任务不存在: #{parent}"); return 1; }

        var nextId = byId.Keys.Where(k => int.TryParse(k, out _)).Select(int.Parse).DefaultIfEmpty(0).Max() + 1;
        var nextOrder = byId.Values.Select(t => t.Order).DefaultIfEmpty(0).Max() + 1;
        var t = new TodoItem
        {
            Id = nextId.ToString(),
            Text = text,
            Parent = parent,
            Date = values.TryGetValue("date", out var d) ? d
                 : parent is not null ? byId[parent].Date
                 : TodoItem.TodayStr(),
            Order = nextOrder,
            Starred = flags.Contains("star"),
        };
        f.Todos.Add(t);
        Save(f);
        NotifyGui();
        Out(_json
            ? JsonSerializer.Serialize(new { id = t.Id, parent = t.Parent, date = t.Date }, JsonOpts)
            : $"已添加 #{t.Id} {t.Text}");
        return 0;
    }

    private static int CmdToggleDone(List<string> pos, bool done)
    {
        var f = Load();
        var byId = Index(f);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var n = 0;
        foreach (var id in Ids(pos))
        {
            if (!byId.TryGetValue(id, out var t)) { Err($"id 不存在: #{id}"); continue; }
            if (t.Completed == done) continue;
            if (done)
            {
                if (t.Running)
                {
                    t.Elapsed += (int)Math.Max(0, now - (long)(t.LastStartedAt ?? 0));
                    t.Running = false;
                    t.LastStartedAt = null;
                }
                t.Date = TodoItem.TodayStr();   // 完成归到当天
            }
            t.Completed = done;
            if (done)
            {
                // 结转承接根完成 → 旧日结转根同步翻篇（与 GUI 语义一致）
                var cur = t.RolledFrom;
                var seen = new HashSet<string>();
                while (cur is not null && seen.Add(cur) && byId.TryGetValue(cur, out var old))
                {
                    if (!old.Completed)
                    {
                        if (old.Running)
                        {
                            old.Elapsed += (int)Math.Max(0, now - (long)(old.LastStartedAt ?? 0));
                            old.Running = false;
                            old.LastStartedAt = null;
                        }
                        old.Completed = true;
                    }
                    cur = old.RolledFrom;
                }
            }
            n++;
        }
        if (n > 0) { Save(f); NotifyGui(); }
        Out(done ? $"已完成 {n} 项" : $"已取消完成 {n} 项");
        return n == 0 ? 1 : 0;
    }

    private static int CmdDelete(List<string> pos)
    {
        var f = Load();
        var byId = Index(f);
        var n = 0;
        foreach (var id in Ids(pos))
        {
            if (!byId.ContainsKey(id)) { Err($"id 不存在: #{id}"); continue; }
            var parents = byId.ToDictionary(kv => kv.Key, kv => kv.Value.Parent);
            var branch = TaskTree.SubtreeWithRoot(parents, id);
            foreach (var t in branch) { f.Todos.Remove(byId[t]); byId.Remove(t); }
            n += branch.Count;
        }
        if (n > 0) { Save(f); NotifyGui(); }
        Out($"已删除 {n} 项（连同子任务）");
        return 0;
    }

    private static int CmdStartPause(List<string> pos, bool start)
    {
        var f = Load();
        var byId = Index(f);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var n = 0;
        foreach (var id in Ids(pos))
        {
            if (!byId.TryGetValue(id, out var t)) { Err($"id 不存在: #{id}"); continue; }
            if (start)
            {
                if (t.Completed) { Err($"#{id} 已完成，不能启动"); continue; }
                if (byId.Values.Any(x => x.Parent == id)) { Err($"#{id} 含子任务，不可单独计时"); continue; }
                if (t.Running) continue;
                t.Running = true;
                t.LastStartedAt = (int)now;
            }
            else
            {
                if (!t.Running) continue;
                t.Elapsed += (int)Math.Max(0, now - (long)(t.LastStartedAt ?? 0));
                t.Running = false;
                t.LastStartedAt = null;
            }
            n++;
        }
        if (n > 0) { Save(f); NotifyGui(); }
        Out($"{(start ? "启动" : "暂停")} {n} 项");
        return 0;
    }

    private static int CmdMove(List<string> pos)
    {
        if (pos.Count < 2) { Err("用法: cli move <id> <yyyy-mm-dd>"); return 2; }
        var f = Load();
        var byId = Index(f);
        if (!byId.TryGetValue(pos[0], out var t)) { Err($"id 不存在: #{pos[0]}"); return 1; }
        t.Date = pos[1];
        Save(f);
        NotifyGui();
        Out($"#{t.Id} 已改期到 {t.Date}");
        return 0;
    }

    private static int CmdRename(List<string> pos)
    {
        if (pos.Count < 2) { Err("用法: cli rename <id> \"新文本\""); return 2; }
        var f = Load();
        var byId = Index(f);
        if (!byId.TryGetValue(pos[0], out var t)) { Err($"id 不存在: #{pos[0]}"); return 1; }
        t.Text = string.Join(' ', pos.Skip(1));
        Save(f);
        NotifyGui();
        Out($"#{t.Id} 已改名");
        return 0;
    }

    private static int CmdStar(List<string> pos, HashSet<string> flags)
    {
        if (pos.Count < 1) { Err("用法: cli star <id> [--on|--off]"); return 2; }
        var f = Load();
        var byId = Index(f);
        if (!byId.TryGetValue(pos[0], out var t)) { Err($"id 不存在: #{pos[0]}"); return 1; }
        t.Starred = flags.Contains("off") ? false : flags.Contains("on") ? true : !t.Starred;
        Save(f);
        NotifyGui();
        Out($"#{t.Id} 重点={(t.Starred ? "开" : "关")}");
        return 0;
    }

    private static int CmdReparent(List<string> pos, Dictionary<string, string> values, HashSet<string> flags)
    {
        if (pos.Count < 1) { Err("用法: cli reparent <id> [--parent 父id | --top]"); return 2; }
        var f = Load();
        var byId = Index(f);
        var id = pos[0];
        if (!byId.TryGetValue(id, out var t)) { Err($"id 不存在: #{id}"); return 1; }
        var newParent = flags.Contains("top") ? null : values.TryGetValue("parent", out var p) ? p : null;
        if (newParent is null && !flags.Contains("top"))
        { Err("需要 --parent 父id 或 --top"); return 2; }
        var parents = byId.ToDictionary(kv => kv.Key, kv => kv.Value.Parent);
        if (!TaskTree.CanAdopt(parents, newParent, id))
        { Err("拒绝: 父不存在/成环/超 3 层"); return 1; }
        t.Parent = newParent;
        t.Order = byId.Values.Select(x => x.Order).DefaultIfEmpty(0).Max() + 1;
        Save(f);
        NotifyGui();
        Out($"#{id} 已移动到 {(newParent is null ? "顶层" : $"#{newParent} 下")}");
        return 0;
    }

    private static int CmdStats()
    {
        var f = Load();
        var total = f.Todos.Count;
        var done = f.Todos.Count(t => t.Completed);
        var running = f.Todos.Count(t => t.Running);
        Out($"全部 {total} 项 · 未完成 {total - done} · 已完成 {done} · 运行中 {running}");
        return 0;
    }

    private static int Err(string msg)
    {
        Console.Error.WriteLine(msg);
        return 1;
    }

    private static int Err(int code, string msg)
    {
        Console.Error.WriteLine(msg);
        return code;
    }

    private static void Out(string msg) => Console.WriteLine(msg);
}
