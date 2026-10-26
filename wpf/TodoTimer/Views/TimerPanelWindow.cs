using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TodoTimer.Models;
using TodoTimer.ViewModels;

namespace TodoTimer.Views;

/// <summary>面板行 VM：时间走绑定刷新（每秒视觉树递归查找是旧实现的卡顿源）。</summary>
public sealed class PanelItemVm : INotifyPropertyChanged
{
    public TodoItem Todo { get; }
    public PanelItemVm(TodoItem t) => Todo = t;

    public string TimeText
    {
        get => _time;
        set { _time = value; OnPropertyChanged(); }
    }
    private string _time = "";

    public bool StarVisible => Todo.Starred;
    public bool Running => Todo.Running;
    public bool Completed => Todo.Completed;

    /// <summary>数据改后全量重读（TodoItem 无 INPC，行内状态靠这里推给绑定）。</summary>
    public void Refresh() => OnPropertyChanged(null);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>悬浮计时面板：置顶无边框、拖标题栏移动（位置持久化）、1s 刷新实时时长。</summary>
public partial class TimerPanelWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _tick;

    public TimerPanelWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        var (x, y) = vm.PanelPosition;
        if (x > 0 || y > 0) { Left = x; Top = y; }
        else { Left = SystemParameters.WorkArea.Width - Width - 24; Top = 24; }
        Deactivated += (_, _) => SavePosition();
        LocationChanged += (_, _) => { if (_dragEnd) SavePosition(); };

        _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) =>
        {
            foreach (var it in _items) it.TimeText = Format(_vm.EffectiveElapsed(it.Todo));
            _vm.CheckReminders();
        };
        _tick.Start();
        Loaded += (_, _) => RebuildItems();
    }

    private bool _dragEnd;

    private void Header_DragMove(object sender, MouseButtonEventArgs e)
    {
        _dragEnd = false;
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
        _dragEnd = true;
        SavePosition();
    }

    private void SavePosition()
    {
        if (double.IsNaN(Left) || double.IsNaN(Top)) return;
        _vm.SavePanelPosition(Left, Top);
    }

    private readonly ObservableCollection<PanelItemVm> _items = new();
    private string? _lastIds;

    /// <summary>成员变化才重建（对比可见 id 序列）；时间由 1s tick 写 TimeText 绑定。</summary>
    public void RefreshItems()
    {
        var ids = string.Join(',', _vm.PanelTodos.Select(t => t.Id));
        if (ids == _lastIds) return;
        RebuildItems();
    }

    private void RebuildItems()
    {
        var todos = _vm.PanelTodos.ToList();
        _lastIds = string.Join(',', todos.Select(t => t.Id));
        _items.Clear();
        foreach (var t in todos)
            _items.Add(new PanelItemVm(t) { TimeText = Format(_vm.EffectiveElapsed(t)) });
        PanelList.ItemsSource = _items;
        PanelList.Visibility = _items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Visibility = _items.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PanelItemVm item)
        {
            _vm.TogglePanelTimerCommand.Execute(item.Todo.Id);
            item.Refresh();
        }
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PanelItemVm item)
        {
            _vm.TogglePanelVisible(item.Todo.Id);
            RebuildItems();
        }
    }

    private void Complete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PanelItemVm item)
        {
            _vm.ToggleCompleteCommand.Execute(item.Todo.Id);
            item.Refresh();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (Application.Current is App { Exiting: true }) return;   // 真退出放行
        e.Cancel = true;                                            // 点 X = 收起，常驻
        Hide();
    }

    internal static string Format(int seconds)
    {
        var h = seconds / 3600;
        var m = seconds % 3600 / 60;
        var s = seconds % 60;
        return h > 0 ? $"{h}:{m:D2}:{s:D2}" : $"{m:D2}:{s:D2}";
    }
}
