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
    private Services.IpcServer? _ipc;
    private TaskbarIcon? _tray;
    private TimerPanelWindow? _panel;
    private DispatcherTimer? _reminderTick;
    private DispatcherTimer? _panelRefreshTick;
    private DispatcherTimer? _syncTick;

    public bool Exiting { get; private set; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string? cls, string? title);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(uint processId);
    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();
    private const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

    protected override void OnStartup(StartupEventArgs e)
    {
        // cli 子命令: headless 执行, 不进 GUI（单实例检测之前）
        if (e.Args.Length > 0 && e.Args[0] == "cli")
        {
            // stdout 被重定向(管道/文件)时绝不能 AttachConsole——它会把进程标准句柄
            // 重置回控制屏(CONOUT$), 覆盖掉重定向句柄 => 零输出。仅交互场景才挂。
            bool redirected;
            try { redirected = Console.IsOutputRedirected; }
            catch { redirected = false; }
            if (!redirected) AttachConsole(ATTACH_PARENT_PROCESS);
            Console.SetOut(new System.IO.StreamWriter(Console.OpenStandardOutput(),
                new System.Text.UTF8Encoding(false)) { AutoFlush = true });
            Console.SetError(new System.IO.StreamWriter(Console.OpenStandardError(),
                new System.Text.UTF8Encoding(false)) { AutoFlush = true });
            var code = Services.CliRunner.Run(e.Args.Skip(1).ToArray());
            if (!redirected) Console.WriteLine();   // 还控制台提示符给 shell（重定向时不污染输出）
            if (!redirected) FreeConsole();
            Environment.ExitCode = code;
            Shutdown(code);
            return;
        }

        // 单实例：已有实例则激活它后退出（--screenshot/--panel-shot 冒烟跳过，允许多开验证）
        var smoke = e.Args.Length > 0 &&
                    (e.Args[0] == "--screenshot" || e.Args[0] == "--panel-shot");
        _mutex = new Mutex(initiallyOwned: true, @"Local\TodoTimer.WPF", out var isNew);
        if (!isNew && !smoke)
        {
            var hwnd = FindWindowW(null, "待办计时器 1.4");
            if (hwnd != IntPtr.Zero) SetForegroundWindow(hwnd);
            Shutdown(0);
            return;
        }
        DispatcherUnhandledException += (_, e2) =>
        {
            File.WriteAllText(@"D:\桌面\scheduleTools\tools\build_tmp\wpf_crash.log",
                e2.Exception.ToString());
            e2.Handled = false;
        };
        base.OnStartup(e);

        var main = new MainWindow { Title = "待办计时器 1.4" };   // 须与 FindWindowW 匹配
        MainWindow = main;
        main.Show();
        var vm = (MainViewModel)main.DataContext;
        vm.AttachSync();                       // v1.5 S2：云同步装配（配置后才实际联网）
        _syncTick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        // 轮询合并必须作用在 vm 的权威列表引用上（绝不能 ToList 副本——改副本=白改），
        // Merged 回调（已切回 UI 线程）负责 Rebuild+Save 落盘
        _syncTick.Tick += (_, _) => _ = vm.Sync?.PollNowAsync(() => vm.AllTodos);
        _syncTick.Start();
        InitTray(vm);
        _ipc = new Services.IpcServer(() => vm.LoadFile());
        _ipc.Start();
        if (e.Args.Any(a => a == "--panel")) TogglePanel();

        // 冒烟钩子：--panel-shot <png> 打开面板离屏渲染后自退（自动验收用）
        var shotIdx = Array.FindIndex(e.Args, a => a == "--panel-shot");
        if (shotIdx >= 0 && shotIdx + 1 < e.Args.Length)
        {
            TogglePanel();
            Dispatcher.InvokeAsync(() =>
            {
                var p = _panel;
                if (p is null) return;
                var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(p);
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    (int)(p.ActualWidth * dpi.DpiScaleX), (int)(p.ActualHeight * dpi.DpiScaleY),
                    dpi.PixelsPerInchX, dpi.PixelsPerInchY,
                    System.Windows.Media.PixelFormats.Pbgra32);
                rtb.Render(p);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                using var fs = File.Create(e.Args[shotIdx + 1]);
                enc.Save(fs);
                Shutdown();
            }, DispatcherPriority.ApplicationIdle);
        }
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
            ToolTipText = "待办计时器 1.4",
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
        _reminderTick.Tick += (_, _) =>
        {
            vm.CheckReminders();
            vm.RunDayRollover();   // 常驻过午夜：自动把昨天的未完成搬到今天（内部幂等+防重入）
        };
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
        _ipc?.Dispose();
        _reminderTick?.Stop();
        _panelRefreshTick?.Stop();
        _syncTick?.Stop();
        _tray?.Dispose();
        _panel?.Close();
        Shutdown();
    }
}
