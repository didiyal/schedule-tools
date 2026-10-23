using System.Collections.ObjectModel;
using System.IO;
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
    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private string statText = "";
    [ObservableProperty] private string newTodoText = "";
    [ObservableProperty] private string? selectedId;
    [ObservableProperty] private string toast = "";

    public ObservableCollection<PlanRow> Rows { get; } = new();
    public string DataDir { get; }

    public MainViewModel(string? dataDir = null)
        => DataDir = dataDir ?? Path.Combine(AppContext.BaseDirectory, "data");

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
        Rebuild();
    }

    private void RebuildIndex()
    {
        _byId = new Dictionary<string, TodoItem>();
        foreach (var t in _file.Todos) _byId[t.Id] = t;
    }

    private Dictionary<string, string?> ParentMap()
        => _byId.ToDictionary(kv => kv.Key, kv => kv.Value.Parent);

    public void Rebuild()
    {
        var sel = SelectedId;
        var rows = RenderPlan.Compute(_file.Todos, EffectiveFilter, SearchText, _collapsed);
        Rows.Clear();
        foreach (var r in rows) Rows.Add(r);

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
        if (parentId != null) Expand(parentId);
        Rebuild();
        Save();
        return t.Id;
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
        t.Completed = !t.Completed;
        if (t.Completed) { t.Running = false; t.LastStartedAt = null; }
        Rebuild();
        Save();
    }

    [RelayCommand]
    private void ToggleStar(string id)
    {
        if (!_byId.TryGetValue(id, out var t)) return;
        t.Starred = !t.Starred;
        Rebuild();
        Save();
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
        Save();
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
        Save();
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
        Save();
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
        Save();
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
        Save();
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

    public DropAction? ResolveDrop(string dragId, string targetId, double yRatio)
        => DropResolver.Resolve(ParentMap(), _byId, dragId, targetId, yRatio);

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
            // 防御（Resolve 已校验过）：兄弟插入仅限同父；顶层维持同日期规则
            if (w.Parent != target.Parent) return false;
            if (w.Parent is null
                && !string.Equals(w.Date, target.Date, StringComparison.Ordinal))
                return false;
            // 视觉兄弟组（顶层=同日期组），排除自身后按目标位置插入，组内 order 重排
            var sibs = _file.Todos
                .Where(t => t.Parent == w.Parent)
                .Where(t => t.Parent is not null
                            || string.Equals(t.Date, w.Date, StringComparison.Ordinal))
                .Where(t => t.Id != dragId)
                .OrderBy(t => t.Order)
                .ToList();
            var idx = sibs.FindIndex(t => t.Id == action.TargetId);
            if (idx < 0) return false;
            sibs.Insert(action.Kind == DropKind.SiblingBefore ? idx : idx + 1, w);
            // 用现有 order 升序值按新视觉序全量重排（比 Python zip 截断版更稳）
            var vals = sibs.Select(t => t.Order).OrderBy(v => v).ToList();
            for (var i = 0; i < sibs.Count; i++) sibs[i].Order = vals[i];
        }
        Rebuild();
        Save();
        return true;
    }

    // ── 存盘 ──

    public void Save()
    {
        _file.Collapsed = _collapsed.OrderBy(x => x, StringComparer.Ordinal).ToList();
        TodoStore.Save(SavePath, _file);
    }

    private static string FormatElapsed(int seconds)
    {
        var h = seconds / 3600;
        var m = seconds % 3600 / 60;
        var s = seconds % 60;
        return h > 0 ? $"{h}小时{m}分" : m > 0 ? $"{m}分{s}秒" : $"{s}秒";
    }
}
