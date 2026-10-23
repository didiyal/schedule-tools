using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TodoTimer.Models;
using TodoTimer.ViewModels;

namespace TodoTimer.Views;

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
        _tick.Tick += (_, _) => { RefreshTimes(); _vm.CheckReminders(); };
        _tick.Start();
        Loaded += (_, _) => RefreshItems();
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

    public void RefreshItems()
    {
        var items = _vm.PanelTodos.ToList();
        PanelList.ItemsSource = items;
        PanelList.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Visibility = items.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        RefreshTimes();
    }

    private void RefreshTimes()
    {
        for (var i = 0; i < PanelList.Items.Count; i++)
        {
            if (PanelList.ItemContainerGenerator.ContainerFromIndex(i) is not ContentPresenter cp) continue;
            if (cp.ContentTemplate.FindName("TimeLbl", cp) is not TextBlock lbl) continue;
            if (cp.Content is not TodoItem t) continue;
            lbl.Text = Format(MainViewModel.LiveElapsed(t));
        }
    }

    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TodoItem t)
            _vm.TogglePanelTimerCommand.Execute(t.Id);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TodoItem t)
            _vm.TogglePanelVisible(t.Id);
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
