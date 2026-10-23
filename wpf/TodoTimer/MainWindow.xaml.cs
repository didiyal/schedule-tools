using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TodoTimer.ViewModels;

namespace TodoTimer;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        Loaded += (_, _) =>
        {
            _vm.LoadFile();
            // 冒烟钩子：--screenshot <png> [--active|--completed] [--search=词]
            // 离屏渲染后自退（无桌面像素依赖，供自动验收）。
            // 必须推迟到 ApplicationIdle：同拍渲染会截在 ItemsSource 布局完成前。
            var args = Environment.GetCommandLineArgs();
            if (args.Length > 2 && args[1] == "--screenshot")
            {
                foreach (var a in args.Skip(2))
                {
                    if (a == "--active") _vm.FilterActive = true;
                    else if (a == "--completed") _vm.FilterCompleted = true;
                    else if (a.StartsWith("--search=", StringComparison.Ordinal))
                        _vm.SearchText = a["--search=".Length..];
                }
                _vm.Rebuild();   // 搜索走 300ms 防抖，冒烟路径同步出结果
                Dispatcher.InvokeAsync(() =>
                {
                    SaveShot(args[2]);
                    File.WriteAllText(args[2] + ".txt",
                        $"rows={_vm.Rows.Count} stat={_vm.StatText}");
                    Application.Current.Shutdown();
                }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
        };
    }

    private void SaveShot(string path)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var rtb = new RenderTargetBitmap(
            (int)(ActualWidth * dpi.DpiScaleX), (int)(ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        rtb.Render(this);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
