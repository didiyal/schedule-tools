using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Hardcodet.Wpf.TaskbarNotification;
using TodoTimer.ViewModels;
using TodoTimer.Views;

namespace TodoTimer;

public partial class App : Application
{
    private Mutex? _mutex;
    private TaskbarIcon? _tray;
    private TimerPanelWindow? _panel;
    private DispatcherTimer? _reminderTick;
    private DispatcherTimer? _panelRefreshTick;

    public bool Exiting { get; private set; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string? cls, string? title);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    protected override void OnStartup(StartupEventArgs e)
    {
        // 单实例：已有实例则激活它后退出（--screenshot 冒烟跳过，允许多开验证）
        var smoke = e.Args.Length > 0 && e.Args[0] == "--screenshot";
        _mutex = new Mutex(initiallyOwned: true, @"Local\TodoTimer.WPF", out var isNew);
        if (!isNew && !smoke)
        {
            var hwnd = FindWindowW(null, "待办计时器");
            if (hwnd != IntPtr.Zero) SetForegroundWindow(hwnd);
            Shutdown(0);
            return;
        }
        base.OnStartup(e);

        var main = new MainWindow { Title = "待办计时器" };
        MainWindow = main;
        main.Show();
        InitTray((MainViewModel)main.DataContext);
    }

    private void InitTray(MainViewModel vm)
    {
        using var stream = Application.GetResourceStream(
            new Uri("pack://application:,,,/app.ico")).Stream;
        var icon = new System.Drawing.Icon(stream);

        var menu = new ContextMenu();
        var show = new MenuItem { Header = "显示主窗口" };
        show.Click += (_, _) => ShowMain();
        var panel = new MenuItem { Header = "计时面板" };
        panel.Click += (_, _) => TogglePanel();
        var auto = new MenuItem { Header = "开机自启", IsCheckable = true, IsChecked = vm.AutostartEnabled };
        auto.Click += (_, _) => vm.AutostartEnabled = auto.IsChecked;
        var quit = new MenuItem { Header = "退出" };
        quit.Click += (_, _) => RealExit();
        menu.Items.Add(show);
        menu.Items.Add(panel);
        menu.Items.Add(new Separator());
        menu.Items.Add(auto);
        menu.Items.Add(new Separator());
        menu.Items.Add(quit);

        _tray = new TaskbarIcon
        {
            ToolTipText = "待办计时器",
            Icon = icon,
            ContextMenu = menu,
        };
        _tray.TrayLeftMouseUp += (_, _) => ShowMain();

        // 目标时长提醒 → 提示音 + 托盘气泡（VM 事件）
        vm.ReminderRaised += (t, target) =>
        {
            System.Media.SystemSounds.Exclamation.Play();
            _tray.ShowBalloonTip("计时提醒",
                $"「{t.Text}」已达 {TimerPanelWindow.Format(target)}",
                BalloonIcon.Warning);
        };
        _reminderTick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _reminderTick.Tick += (_, _) => vm.CheckReminders();
        _reminderTick.Start();
    }

    private void ShowMain()
    {
        var main = MainWindow;
        if (main is null) return;
        main.Show();
        if (main.WindowState == WindowState.Minimized) main.WindowState = WindowState.Normal;
        main.Activate();
    }

    public void TogglePanel()
    {
        if (MainWindow is not MainWindow main) return;
        var vm = main.DataContext as MainViewModel;
        if (vm is null) return;
        if (_panel is null)
        {
            _panel = new TimerPanelWindow(vm);
            _panel.Closed += (_, _) => _panel = null;
            _panelRefreshTick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _panelRefreshTick.Tick += (_, _) => _panel?.RefreshItems();
            _panelRefreshTick.Start();
        }
        if (_panel.IsVisible) _panel.Hide();
        else { _panel.RefreshItems(); _panel.Show(); }
    }

    public void RealExit()
    {
        Exiting = true;
        _reminderTick?.Stop();
        _panelRefreshTick?.Stop();
        _tray?.Dispose();
        _panel?.Close();
        Shutdown();
    }
}
