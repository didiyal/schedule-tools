using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TodoTimer.Models;

namespace TodoTimer.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private TodoFile _file = new();
    private Dictionary<string, TodoItem> _byId = new();
    private HashSet<string> _collapsed = new();
    private CancellationTokenSource? _searchDebounce;
    private List<TodoItem>? _undoSnapshot;          // 删整枝的父先子后快照
    private int _nextId;                            // 已用最大数字 id（新建 = +1）
    private int _nextOrder;                         // 全局 order 计数器（对齐 Python _next_order）

    [ObservableProperty] private bool filterActive;
    [ObservableProperty] private bool filterCompleted;
    [ObservableProperty] private bool filterToday;
    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private string statText = "";
    [ObservableProperty] private string newTodoText = "";
    [ObservableProperty] private string? selectedId;
    [ObservableProperty] private string toast = "";
    [ObservableProperty] private bool autostartEnabled;
    private bool _suppressAutostartSync;

    public ObservableCollection<PlanRow> Rows { get; private set; } = new();
    public string DataDir { get; }
    public Services.SyncService? Sync { get; private set; }   // v1.5 S2：云同步（配置后才非空可用）

    public MainViewModel(string? dataDir = null)
        => DataDir = dataDir ?? ResolveDataDir();

    /// <summary>App 启动时装配同步服务（数据目录就绪后调用一次）。</summary>
    public void AttachSync()
    {
        Sync ??= new Services.SyncService(DataDir);
        // 合并发生在后台线程（改 _file.Todos 引用内容），必须回 UI 线程再 Rebuild+Save
        Sync.Merged += () => System.Windows.Application.Current?.Dispatcher
            .BeginInvoke(() => { Rebuild(); Save(); });
        // 首次登记指纹（已有状态则保留），避免把存量数据误判为新改动
        Sync.InitFingerprints(_file.Todos);
        Sync.StatusChanged += s => System.Windows.Application.Current?.Dispatcher
            .BeginInvoke(() => SyncStatus = s);
        SyncStatus = Sync.Status;
    }

    [ObservableProperty] private string syncStatus = "";

    /// <summary>便携语义同 Python：打包版读 EXE 旁 data/；开发构建读仓库 data/。</summary>
    public static string ResolveDataDir()
    {
        var baseDir = AppContext.BaseDirectory;
        var binDebug = "bin\\Debug\\";
        var binRelease = "bin\\Release\\";
        if (baseDir.Contains(binDebug) || baseDir.Contains(binRelease))
        {
            var repo = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", ".."));
            return Path.Combine(repo, "data");
        }
        return Path.Combine(baseDir, "data");
    }

    /// <summary>未完成/已完成双勾或都不勾 = 全部（v1.2 语义）。</summary>
    private FilterState EffectiveFilter =>
        FilterActive == FilterCompleted ? FilterState.All
        : FilterActive ? FilterState.Active
        : FilterState.Completed;

    private string SavePath => Path.Combine(DataDir, "todos.json");
    private string ArchivePath => Path.Combine(DataDir, "archive.json");

    public void LoadFile()
    {
        _file = TodoStore.Load(SavePath) ?? new TodoFile();
        _collapsed = new HashSet<string>(_file.Collapsed ?? new List<string>());
        RebuildIndex();
        _nextId = 0;
        _nextOrder = 1;
        foreach (var t in _file.Todos)
        {
            if (int.TryParse(t.Id, out var n) && n > _nextId) _nextId = n;
            if (t.Order >= _nextOrder) _nextOrder = t.Order + 1;
        }
        CatchupOffline();
        Rebuild();
        // 自启意图落盘自愈：即使 Run 键被清理工具删掉也能恢复（对齐 Python）
        _suppressAutostartSync = true;
        AutostartEnabled = _file.Autostart;
        _suppressAutostartSync = false;
        Services.Autostart.Set(_file.Autostart);
    }

    private void RebuildIndex()
    {
        _byId = new Dictionary<string, TodoItem>();
        foreach (var t in _file.Todos) _byId[t.Id] = t;
    }

    private Dictionary<string, string?> ParentMap()
        => _byId.ToDictionary(kv => kv.Key, kv => kv.Value.Parent);

    private string? _previewDragId;
    private DropAction? _previewDrop;
    private string? _previewKey;

    /// <summary>拖拽预览(push 模式): 被拖项离位, 目标处开出空白槽, 其他卡片推开。</summary>
    private string? _previewBlockedTarget;

    public void ShowDropPreview(string dragId, DropAction? action)
    {
        var key = $"{dragId}:{action?.TargetId}:{action?.Kind}";
        if (key == _previewKey) return;   // 同一落点不重复重排
        _previewKey = key;
        _previewDragId = dragId;
        _previewDrop = action;
        _previewBlockedTarget = action is null ? key : null;
        ApplyPreviewLive();
    }

    /// <summary>拖拽期间对现有集合做最小变动（移除被拖行+插占位槽）。
    /// 不能整表替换 ItemsSource：容器重建会触发 OLE DragLeave → 清预览的死循环。</summary>
    private void ApplyPreviewLive()
    {
        if (_previewDragId is null) return;
        if (_previewDrop is null)
        {
            // 无效落点：被拖项离位 + 红色虚线槽（"这里放不进去"），反馈不能是零
            for (var i = Rows.Count - 1; i >= 0; i--)
                if (Rows[i].IsPlaceholder || Rows[i].Todo.Id == _previewDragId)
                    Rows.RemoveAt(i);
            Rows.Add(new PlanRow { IsPlaceholder = true, IsBlocked = true });
            return;
        }
        for (var i = Rows.Count - 1; i >= 0; i--)
            if (Rows[i].IsPlaceholder || Rows[i].Todo.Id == _previewDragId)
                Rows.RemoveAt(i);
        var idx = -1;
        for (var i = 0; i < Rows.Count; i++)
            if (!Rows[i].IsHeader && Rows[i].Todo.Id == _previewDrop.TargetId) { idx = i; break; }
        if (idx < 0) return;
        var target = Rows[idx];
        var depth = _previewDrop.Kind switch
        {
            DropKind.Child => target.Depth + 1,
            DropKind.Promote => 0,       // 提升为主任务: 槽顶格
            _ => target.Depth,
        };
        var before = _previewDrop.Kind == DropKind.SiblingBefore
                     || (_previewDrop.Kind == DropKind.Promote && _previewDrop.Before);
        Rows.Insert(before ? idx : idx + 1,
            new PlanRow { IsPlaceholder = true, Depth = depth,
                CardMargin = new Thickness(depth * 28, 2, 0, 2) });
    }

    /// <summary>静默清预览状态（不重建），供 Drop 路径先重置再 ExecuteDrop。</summary>
    public void ResetPreviewState()
    {
        _previewKey = null;
        _previewDragId = null;
        _previewDrop = null;
        _previewBlockedTarget = null;
    }

    public void ClearDropPreview()
    {
        if (_previewKey is null) return;
        _previewKey = null;
        _previewDragId = null;
        _previewDrop = null;
        Rebuild();
    }

    private void ApplyPreview(List<PlanRow> rows)
    {
        if (_previewDragId is null || _previewDrop is null) return;
        rows.RemoveAll(r => !r.IsHeader && r.Todo.Id == _previewDragId);
        var idx = rows.FindIndex(r => !r.IsHeader && r.Todo.Id == _previewDrop.TargetId);
        if (idx < 0) return;
        var target = rows[idx];
        var depth = _previewDrop.Kind == DropKind.Child ? target.Depth + 1 : target.Depth;
        rows.Insert(_previewDrop.Kind == DropKind.SiblingBefore ? idx : idx + 1,
            new PlanRow { IsPlaceholder = true, Depth = depth,
                CardMargin = new Thickness(depth * 28, 2, 0, 2) });
    }

    public void Rebuild()
    {
        var sel = SelectedId;
        var rows = RenderPlan.Compute(_file.Todos, EffectiveFilter, SearchText, _collapsed, todayOnly: FilterToday);
        ApplyPreview(rows);
        // 整表替换：单次 Reset 通知（Clear+逐条 Add 会触发每行一次重排，165 行明显卡顿）
        Rows = new ObservableCollection<PlanRow>(rows);
        OnPropertyChanged(nameof(Rows));

        var total = _file.Todos.Count;
        var done = _file.Todos.Count(t => t.Completed);
        var running = _file.Todos.Count(t => t.Running);
        StatText = $"全部 {total} 项 · 未完成 {total - done} · 已完成 {done} · 运行中 {running}";

        // 重建后按 id 恢复选中（Clear 会先吞掉选择）
        SelectedId = null;
        SelectedId = sel != null && _byId.ContainsKey(sel) ? sel : null;
    }

    partial void OnFilterActiveChanged(bool value) => Rebuild();
    partial void OnFilterCompletedChanged(bool value) => Rebuild();
    partial void OnFilterTodayChanged(bool value) => Rebuild();

    partial void OnAutostartEnabledChanged(bool value)
    {
        if (_suppressAutostartSync) return;
        Services.Autostart.Set(value);
        _file.Autostart = value;
        Save();
    }

    partial void OnSearchTextChanged(string value)
    {
        _searchDebounce?.Cancel();
        var cts = _searchDebounce = new CancellationTokenSource();
        _ = DebouncedRebuildAsync(cts.Token);
    }

    private async Task DebouncedRebuildAsync(CancellationToken ct)
    {
        try { await Task.Delay(300, ct); }
        catch (TaskCanceledException) { return; }
        Rebuild();
    }

    private void ShowToast(string msg)
    {
        Toast = msg;
        _ = ClearToastAsync();
    }

    private async Task ClearToastAsync()
    {
        await Task.Delay(2600);
        Toast = "";
    }

    // ── 新建 ──

    [RelayCommand]
    private void Add()
    {
        var id = AddTodo(NewTodoText, parentId: null, out var err);
        if (id != null) NewTodoText = "";
        else if (err != null) ShowToast(err);
    }

    /// <summary>新建待办。子任务继承父日期、追加为父最后一个子级（对齐 Python）。</summary>
    public string? AddTodo(string? text, string? parentId, out string? err, string? date = null,
        bool starred = false, bool completed = false)
    {
        err = null;
        text = text?.Trim();
        if (string.IsNullOrEmpty(text)) { err = ""; return null; }   // 空文本静默（同输入框直接回车）
        if (parentId != null)
        {
            if (!_byId.ContainsKey(parentId)) { err = "父任务不存在"; return null; }
            if (TaskTree.DepthOf(ParentMap(), parentId) + 1 > TaskTree.MaxDepthIndex)
            {
                err = "已达最大层级（3 层）";
                return null;
            }
        }
        var t = new TodoItem
        {
            Id = (++_nextId).ToString(),
            Text = text,
            Parent = parentId,
            Date = date ?? (parentId != null ? _byId[parentId].Date : TodoItem.TodayStr()),
            Order = _nextOrder++,
            Starred = starred,
            Completed = completed,
        };
        _file.Todos.Add(t);
        _byId[t.Id] = t;
        if (parentId != null)
        {
            Expand(parentId);
            // 首次建子任务: 父任务的时间/面板身份迁移给同名子任务, 父归零
            // (含子任务任务不可计时/不入面板——迁移后才满足规则)
            var hadChildren = _file.Todos.Count(x => x.Parent == parentId && x.Id != t.Id) > 0;
            if (!hadChildren) MigrateParentToChild(parentId);
        }
        Rebuild();
        ScheduleSave();
        return t.Id;
    }

    /// <summary>首次建子任务时: 父任务若有时长/在跑/在面板, 生成同名子任务承接时长,
    /// 父任务归零并移出面板（用户规则: 有子任务者时长=子任务之和且不可入面板）。</summary>
    private void MigrateParentToChild(string parentId)
    {
        if (!_byId.TryGetValue(parentId, out var p)) return;
        if (!p.Running && p.Elapsed <= 0 && !p.Visible) return;   // 无可迁移
        if (p.Running) { p.Elapsed = LiveElapsed(p); p.Running = false; p.LastStartedAt = null; }
        var twin = new TodoItem
        {
            Id = (++_nextId).ToString(),
            Text = p.Text,
            Parent = parentId,
            Date = p.Date,
            Order = _nextOrder++,
            Elapsed = p.Elapsed,
        };
        _file.Todos.Add(twin);
        _byId[twin.Id] = twin;
        p.Elapsed = 0;
        p.Visible = false;
    }

    // ── 折叠 ──

    [RelayCommand]
    private void ToggleExpand(string id)
    {
        if (!_byId.ContainsKey(id)) return;
        if (_collapsed.Add(id))
            (_file.Collapsed ??= new List<string>()).Add(id);
        else
        {
            _collapsed.Remove(id);
            _file.Collapsed?.Remove(id);
        }
        Rebuild();
    }

    private void Expand(string id)
    {
        if (_collapsed.Remove(id)) _file.Collapsed?.Remove(id);
    }

    // ── 完成 / 重点 ──

    [RelayCommand]
    private void ToggleComplete(string id)
    {
        if (!_byId.TryGetValue(id, out var t)) return;
        if (t.Completed)
        {
            t.Completed = false;                        // set_incomplete：仅翻标记
        }
        else
        {
            // set_complete 语义：停表 + 完成归到当天
            if (t.Running) { t.Elapsed = LiveElapsed(t); t.Running = false; t.LastStartedAt = null; }
            t.Completed = true;
            t.Date = TodoItem.TodayStr();
        }
        Rebuild();
        ScheduleSave();
    }

    [RelayCommand]
    private void ToggleStar(string id)
    {
        if (!_byId.TryGetValue(id, out var t)) return;
        t.Starred = !t.Starred;
        Rebuild();
        ScheduleSave();
    }

    // ── 层级（Tab / Shift+Tab，语义对齐 Python _indent/_outdent_selected）──

    public bool Indent(string id)
    {
        if (!_byId.TryGetValue(id, out var w)) return false;
        var parents = ParentMap();
        var sibs = SiblingsOf(w.Parent);
        var i = sibs.FindIndex(t => t.Id == id);
        if (i <= 0) return false;                       // 没有上一兄弟，无从降级
        var target = sibs[i - 1];
        if (!TaskTree.CanAdopt(parents, target.Id, id))
        {
            ShowToast("已达最大层级（3 层）");
            return false;
        }
        w.Parent = target.Id;
        w.Order = _nextOrder++;                         // 成为该父的最后一个子任务
        Expand(target.Id);
        Rebuild();
        ScheduleSave();
        return true;
    }

    public bool Outdent(string id)
    {
        if (!_byId.TryGetValue(id, out var w)) return false;
        var pid = w.Parent;
        if (pid is null || !_byId.TryGetValue(pid, out var parent)) return false;
        var grandpa = parent.Parent;
        var parentSibs = SiblingsOf(grandpa);
        var i = parentSibs.FindIndex(t => t.Id == pid);
        var nxt = i + 1 < parentSibs.Count ? parentSibs[i + 1] : null;
        var lo = parent.Order;
        var hi = nxt?.Order ?? lo + 2;
        w.Parent = grandpa;
        if (hi - lo >= 2)
        {
            w.Order = (lo + hi) / 2;                    // 插到父与下一兄弟的空隙
        }
        else
        {
            // 无空隙：紧跟父，其后兄弟整体后移（组内相对序不变）
            w.Order = lo + 1;
            for (var j = 0; j < parentSibs.Count - 1 - i; j++)
                parentSibs[i + 1 + j].Order = hi + 1 + j;
        }
        Rebuild();
        ScheduleSave();
        return true;
    }

    /// <summary>兄弟列表（同父；顶层不分日期），按 order 升序。</summary>
    private List<TodoItem> SiblingsOf(string? pid)
        => _file.Todos.Where(t => t.Parent == pid).OrderBy(t => t.Order).ToList();

    [RelayCommand] private void IndentSelected() => Indent(SelectedId ?? "");
    [RelayCommand] private void OutdentSelected() => Outdent(SelectedId ?? "");

    // ── 删除整枝 + 撤销（对齐 Python _delete_selected/_key_undo）──

    public (bool NeedConfirm, string Message) PeekDelete(string id)
    {
        var parents = ParentMap();
        var branch = TaskTree.SubtreeWithRoot(parents, id);
        var kids = branch.Count - 1;
        var elapsed = branch.Sum(t => _byId.TryGetValue(t, out var x) ? x.Elapsed : 0);
        if (elapsed <= 0 && kids <= 0) return (false, "");
        var w = _byId[id];
        var msg = kids > 0 ? $"确定删除「{w.Text}」及其 {kids} 个子任务吗？"
                           : $"确定删除「{w.Text}」吗？";
        if (elapsed > 0)
            msg += $"\n共记录 {FormatElapsed(elapsed)}。";
        return (true, msg);
    }

    public void Delete(string id)
    {
        if (!_byId.ContainsKey(id)) return;
        var parents = ParentMap();
        // 撤销快照：父先子后（恢复时按序重建引用）
        _undoSnapshot = new List<TodoItem> { _byId[id] };
        var desc = TaskTree.Descendants(parents, id)
            .OrderBy(t => _byId[t].Order).ToList();
        foreach (var d in desc) _undoSnapshot.Add(_byId[d]);

        foreach (var t in TaskTree.SubtreeWithRoot(parents, id))
        {
            _file.Todos.Remove(_byId[t]);
            _byId.Remove(t);
        }
        SelectedId = null;
        Rebuild();
        ScheduleSave();
    }

    public bool HasUndo => _undoSnapshot != null;

    public bool Undo()
    {
        if (_undoSnapshot is null) return false;
        foreach (var t in _undoSnapshot)               // 父先子后
        {
            _file.Todos.Add(t);
            _byId[t.Id] = t;
            if (int.TryParse(t.Id, out var n) && n > _nextId) _nextId = n;
            if (t.Order >= _nextOrder) _nextOrder = t.Order + 1;
        }
        _undoSnapshot = null;
        Rebuild();
        ScheduleSave();
        return true;
    }

    [RelayCommand] private void DeleteSelected()
    {
        var id = SelectedId;
        if (id is null) return;
        var (need, msg) = PeekDelete(id);
        if (need)
        {
            var r = System.Windows.MessageBox.Show(msg, "确认删除",
                System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
            if (r != System.Windows.MessageBoxResult.Yes) return;
        }
        Delete(id);
    }

    [RelayCommand] private void UndoSelected() => Undo();

    [RelayCommand]
    private void ExitApp()
    {
        Save();   // 冲刷防抖中的未落盘改动
        (Application.Current as App)?.RealExit();
    }

    // ── 归档（对齐 Python _archive_completed：未完成后代上抛最近未归档祖先）──

    public int CompletedCount() => _file.Todos.Count(t => t.Completed);

    public int ArchiveCompleted()
    {
        var completedIds = _file.Todos.Where(t => t.Completed).Select(t => t.Id).ToList();
        if (completedIds.Count == 0) return 0;
        var completedSet = new HashSet<string>(completedIds);
        var parents = ParentMap();

        // 被归档节点的未完成后代上抛给最近的未归档祖先（保留未完结工作）
        foreach (var tid in completedIds)
        {
            foreach (var d in TaskTree.Descendants(parents, tid))
            {
                if (!_byId.TryGetValue(d, out var dw) || dw.Completed || completedSet.Contains(d))
                    continue;
                var seen = new HashSet<string>();
                var cur = parents.TryGetValue(d, out var p0) ? p0 : null;
                while (cur is not null && completedSet.Contains(cur) && seen.Add(cur))
                    cur = parents.TryGetValue(cur, out var p) ? p : null;
                dw.Parent = _byId.ContainsKey(cur ?? "") ? cur : null;
                dw.Order = _nextOrder++;
            }
        }

        var archive = TodoStore.LoadArchive(ArchivePath);
        foreach (var tid in completedIds)
        {
            var w = _byId[tid];
            w.ArchivedAt = TodoItem.TodayStr();
            archive.Add(w);
            _file.Todos.Remove(w);
            _byId.Remove(tid);
        }
        TodoStore.SaveArchive(ArchivePath, archive);
        SelectedId = null;
        Rebuild();
        ScheduleSave();
        return completedIds.Count;
    }

    [RelayCommand]
    private void ArchiveCompletedConfirmed()
    {
        var n = CompletedCount();
        if (n == 0) return;
        var r = System.Windows.MessageBox.Show(
            $"将 {n} 条已完成待办移到归档文件？\n归档后可在 data/archive.json 中查看。",
            "确认归档", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        if (r != System.Windows.MessageBoxResult.Yes) return;
        ArchiveCompleted();
    }

    // ── 拖拽落点（解析=纯函数 DropResolver；应用=ExecuteDrop）──

    public TodoItem? GetTodo(string id) => _byId.TryGetValue(id, out var t) ? t : null;

    /// <summary>被拖任务身后跟着的后代数（整枝体积角标）。</summary>
    public int SubtreeExtraCount(string id)
        => _byId.ContainsKey(id) ? TaskTree.Descendants(ParentMap(), id).Count : 0;

    public DropAction? ResolveDrop(string dragId, string targetId, DropIntent intent, double yRatio)
        => DropResolver.Resolve(ParentMap(), _byId, dragId, targetId, intent, yRatio);

    public bool ExecuteDrop(string dragId, DropAction action)
    {
        if (!_byId.TryGetValue(dragId, out var w)) return false;
        if (!_byId.TryGetValue(action.TargetId, out var target)) return false;

        if (action.Kind == DropKind.Child)
        {
            w.Parent = action.TargetId;
            Expand(action.TargetId);                    // 目标自动展开
            w.Order = _nextOrder++;                     // 挂为目标最后一个子任务
        }
        else
        {
            // 同级插入 / 提升为主任务：落进目标所在日组（跨日期=顺带改期）
            if (action.Kind == DropKind.Promote) w.Parent = null;
            else if (w.Parent != target.Parent) return false;   // 防御(Resolve 已校验)
            w.Date = target.Date;
            var groupDate = target.Date;
            var sibs = _file.Todos
                .Where(t => t.Parent == (action.Kind == DropKind.Promote ? null : w.Parent))
                .Where(t => t.Parent is not null
                            || string.Equals(t.Date, groupDate, StringComparison.Ordinal))
                .Where(t => t.Id != dragId)
                .OrderBy(t => t.Order)
                .ToList();
            var idx = sibs.FindIndex(t => t.Id == action.TargetId);
            if (idx < 0) return false;
            var before = action.Kind == DropKind.SiblingBefore
                         || (action.Kind == DropKind.Promote && action.Before);
            sibs.Insert(before ? idx : idx + 1, w);
            // 用现有 order 升序值按新视觉序全量重排（比 Python zip 截断版更稳）
            var vals = sibs.Select(t => t.Order).OrderBy(v => v).ToList();
            for (var i = 0; i < sibs.Count; i++) sibs[i].Order = vals[i];
        }
        Rebuild();
        ScheduleSave();
        return true;
    }

    // ── 计时（对齐 Python TodoWidget.start/pause/get_live_elapsed）──

    public static int LiveElapsed(TodoItem t)
    {
        if (!t.Running || t.LastStartedAt is null) return t.Elapsed;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return t.Elapsed + (int)Math.Max(0, now - t.LastStartedAt.Value);
    }

    private bool HasChildren(string id) => _file.Todos.Any(t => t.Parent == id);

    /// <summary>含子任务的任务: 时长=自身+子任务递归之和（用户拍板, 不可单独计时）。</summary>
    public int EffectiveElapsed(TodoItem t) => EffectiveElapsed(t, new HashSet<string>());

    private int EffectiveElapsed(TodoItem t, HashSet<string> seen)
    {
        var total = LiveElapsed(t);
        if (!seen.Add(t.Id)) return total;
        foreach (var c in _file.Todos.Where(x => x.Parent == t.Id))
            total += EffectiveElapsed(c, seen);
        return total;
    }

    public void StartTimer(string id)
    {
        if (!_byId.TryGetValue(id, out var t)) return;
        if (t.Completed || t.Running) return;
        if (HasChildren(id))
        {
            ShowToast("含子任务的任务不可单独计时（时长为子任务之和）");
            return;
        }
        t.Running = true;
        t.LastStartedAt = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Rebuild();
        ScheduleSave();
    }

    public void PauseTimer(string id)
    {
        if (!_byId.TryGetValue(id, out var t) || !t.Running) return;
        t.Elapsed = LiveElapsed(t);
        t.Running = false;
        t.LastStartedAt = null;
        Rebuild();
        ScheduleSave();
    }

    [RelayCommand]
    private void TogglePanelTimer(string id)
    {
        if (!_byId.TryGetValue(id, out var t)) return;
        if (t.Running) PauseTimer(id); else StartTimer(id);
    }

    /// <summary>加入/移出计时面板；移出时停表（对齐 Python hide()）。</summary>
    public void TogglePanelVisible(string id)
    {
        if (!_byId.TryGetValue(id, out var t)) return;
        if (t.Visible)
        {
            if (t.Running) PauseTimer(id);
            t.Visible = false;
        }
        else
        {
            if (HasChildren(id))
            {
                ShowToast("含子任务的任务不能加入计时面板（时长为子任务之和）");
                return;
            }
            t.Visible = true;
        }
        Rebuild();
        ScheduleSave();
    }

    /// <summary>离线补算：载入时运行中的任务把停机时长计入（对齐 Python 装载恢复）。</summary>
    private void CatchupOffline()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var t in _file.Todos)
        {
            if (t.Completed) t.Running = false;
            if (t.Running && t.LastStartedAt is not null)
            {
                t.Elapsed += (int)Math.Max(0, now - t.LastStartedAt.Value);
                t.LastStartedAt = (int)now;
            }
        }
    }

    /// <summary>把任务改期到今天并提升为顶层（今日空槽的拖放落点）。</summary>
    public void MoveToToday(string id)
    {
        if (!_byId.TryGetValue(id, out var t)) return;
        if (t.Running) { t.Elapsed = LiveElapsed(t); t.Running = false; t.LastStartedAt = null; }
        t.Parent = null;
        t.Date = TodoItem.TodayStr();
        t.Order = _nextOrder++;
        Rebuild();
        Save();
    }

    /// <summary>目标时长提醒检查（由面板/主窗 1s tick 调用）。</summary>
    public event Action<TodoItem, int>? ReminderRaised;

    public void CheckReminders()
    {
        foreach (var t in _file.Todos)
        {
            if (!t.Running || t.TargetSeconds is null || t.Reminded) continue;
            if (LiveElapsed(t) >= t.TargetSeconds.Value)
            {
                t.Reminded = true;
                ReminderRaised?.Invoke(t, t.TargetSeconds.Value);
            }
        }
    }

    public void SetReminder(string id, int seconds)
    {
        if (!_byId.TryGetValue(id, out var t)) return;
        t.TargetSeconds = seconds;
        t.Reminded = false;
        Rebuild();
        ScheduleSave();
    }

    public void ClearReminder(string id)
    {
        if (!_byId.TryGetValue(id, out var t)) return;
        t.TargetSeconds = null;
        t.Reminded = false;
        Rebuild();
        ScheduleSave();
    }

    // ── 计时面板 ──

    public IEnumerable<TodoItem> PanelTodos => _file.Todos.Where(t => t.Visible);

    /// <summary>全量任务列表（同步合并的目标集合；与 _file.Todos 同引用）。</summary>
    public List<TodoItem> AllTodos => _file.Todos;
    public (int X, int Y) PanelPosition => (_file.PanelX, _file.PanelY);

    public void SavePanelPosition(double x, double y)
    {
        _file.PanelX = (int)x;
        _file.PanelY = (int)y;
        Save();
    }

    // ── 存盘 ──

    public void Save()
    {
        _file.Collapsed = _collapsed.OrderBy(x => x, StringComparer.Ordinal).ToList();
        // 运行中的任务把实时时长固化进 elapsed、last_started_at 同步为现在，
        // 重载补算 = live + (载入时刻 - 现在)，不重复计时
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var t in _file.Todos)
        {
            if (t.Running && t.LastStartedAt is not null)
            {
                t.Elapsed = LiveElapsed(t);
                t.LastStartedAt = (int)now;
            }
        }
        TodoStore.Save(SavePath, _file);
        Sync?.OnSaved(() => _file.Todos);   // 同步开启时：存盘 → 推送变更（异步）
    }

    private System.Windows.Threading.DispatcherTimer? _saveTimer;

    /// <summary>存盘防抖：连续操作合并为一次落盘（对齐 Python _schedule_save）。</summary>
    public void ScheduleSave()
    {
        if (_saveTimer is null)
        {
            _saveTimer = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromMilliseconds(400) };
            _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Save(); };
        }
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private static string FormatElapsed(int seconds)
    {
        var h = seconds / 3600;
        var m = seconds % 3600 / 60;
        var s = seconds % 60;
        return h > 0 ? $"{h}小时{m}分" : m > 0 ? $"{m}分{s}秒" : $"{s}秒";
    }
}
