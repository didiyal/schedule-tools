using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TodoTimer.Models;

namespace TodoTimer;

public partial class MainWindow : Window
{
    // M0 出口判据：读 EXE 同目录 data/todos.json（便携语义同 Python 版）渲染平铺列表
    private int _todoCount = -1;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            var rows = LoadData();
            // 冒烟钩子：--screenshot <png> 离屏渲染后自退（无桌面像素依赖，供自动验收）。
            // 必须推迟到 ApplicationIdle：同拍渲染会截在 ItemsSource 布局完成前（截出全空白）。
            var args = Environment.GetCommandLineArgs();
            if (args.Length > 2 && args[1] == "--screenshot")
            {
                Dispatcher.InvokeAsync(() =>
                {
                    SaveShot(args[2]);
                    File.WriteAllText(args[2] + ".txt",
                        $"todos={_todoCount} rows={rows.Count} ticks={Environment.TickCount64}");
                    Application.Current.Shutdown();
                }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
        };
    }

    private List<TodoRow> LoadData()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "data", "todos.json");
        var file = TodoStore.Load(path);
        var rows = new List<TodoRow>();
        _todoCount = file?.Todos.Count ?? 0;

        if (file is not null)
        {
            var parents = new Dictionary<string, string?>();
            foreach (var t in file.Todos) parents[t.Id] = t.Parent;

            var today = TodoItem.TodayStr();
            foreach (var g in file.Todos.GroupBy(t => t.Date).OrderByDescending(g => g.Key))
            {
                var label = g.Key == today ? "今日" : g.Key;
                rows.Add(new TodoRow { IsHeader = true, Label = $"▎{label} · {g.Count()}项" });
                foreach (var t in g.OrderByDescending(t => t.Starred).ThenBy(t => t.Order))
                {
                    var star = t.Starred ? "★ " : "";
                    var mark = t.Completed ? "[已完成] " : "";
                    var live = t.Running ? " ⏱" : "";
                    rows.Add(new TodoRow
                    {
                        Label = $"{star}{mark}{t.Text}{live}",
                        Indent = new Thickness(TaskTree.DepthOf(parents, t.Id) * 18, 2, 0, 2),
                        Completed = t.Completed,
                    });
                }
            }
        }
        else
        {
            rows.Add(new TodoRow { IsHeader = true, Label = "（暂无数据）" });
        }

        List.ItemsSource = rows;
        return rows;
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

/// <summary>M0 平铺行。M1 起换 MVVM + 树形渲染计划。</summary>
public sealed class TodoRow
{
    public bool IsHeader { get; init; }
    public string Label { get; init; } = "";
    public Thickness Indent { get; init; }
    public bool Completed { get; init; }
}
