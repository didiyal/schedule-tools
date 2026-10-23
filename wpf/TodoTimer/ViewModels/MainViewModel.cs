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

    [ObservableProperty] private bool filterActive;
    [ObservableProperty] private bool filterCompleted;
    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private string statText = "";

    public ObservableCollection<PlanRow> Rows { get; } = new();

    /// <summary>未完成/已完成双勾或都不勾 = 全部（v1.2 语义）。</summary>
    private FilterState EffectiveFilter =>
        FilterActive == FilterCompleted ? FilterState.All
        : FilterActive ? FilterState.Active
        : FilterState.Completed;

    public string DataDir { get; } =
        Path.Combine(AppContext.BaseDirectory, "data");

    public void LoadFile()
    {
        _file = TodoStore.Load(Path.Combine(DataDir, "todos.json")) ?? new TodoFile();
        _collapsed = new HashSet<string>(_file.Collapsed ?? new List<string>());
        RebuildIndex();
        Rebuild();
    }

    private void RebuildIndex()
    {
        _byId = new Dictionary<string, TodoItem>();
        foreach (var t in _file.Todos) _byId[t.Id] = t;
    }

    public void Rebuild()
    {
        var rows = RenderPlan.Compute(_file.Todos, EffectiveFilter, SearchText, _collapsed);
        Rows.Clear();
        foreach (var r in rows) Rows.Add(r);

        var total = _file.Todos.Count;
        var done = _file.Todos.Count(t => t.Completed);
        var running = _file.Todos.Count(t => t.Running);
        StatText = $"全部 {total} 项 · 未完成 {total - done} · 已完成 {done} · 运行中 {running}";
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

    public void Save()
    {
        _file.Collapsed = _collapsed.OrderBy(x => x, StringComparer.Ordinal).ToList();
        TodoStore.Save(Path.Combine(DataDir, "todos.json"), _file);
    }
}
