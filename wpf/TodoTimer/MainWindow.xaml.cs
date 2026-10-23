using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TodoTimer.Models;
using TodoTimer.ViewModels;
using TodoTimer.Views;

namespace TodoTimer;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private DropAdorner? _dropAdorner;
    private Point _downPt;
    private string? _downRowId;
    private string? _dragId;
    private DropAction? _pendingDrop;

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

    // ── 拖拽三区（解析在 DropResolver 纯函数，这里只做命中与视觉）──

    private void List_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _downPt = e.GetPosition(List);
        var row = RowFromPoint(_downPt).Row;
        _downRowId = row is { IsHeader: false } ? row.Todo.Id : null;
    }

    private void List_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _downRowId is null || _dragId is not null)
            return;
        var pt = e.GetPosition(List);
        if (Math.Abs(pt.X - _downPt.X) < 4 && Math.Abs(pt.Y - _downPt.Y) < 4) return;
        _dragId = _downRowId;
        DragDrop.DoDragDrop(List, new DataObject(DataFormats.StringFormat, _dragId), DragDropEffects.Move);
        _dragId = null;
        _pendingDrop = null;
        Adorner()?.Clear();
    }

    private void List_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        _dragId ??= e.Data.GetData(DataFormats.StringFormat) as string;
        if (_dragId is null) return;
        var pt = e.GetPosition(List);
        var (row, yRatio, rect) = RowFromPoint(pt);
        if (row?.Todo is null || row.IsHeader) { Adorner()?.Clear(); return; }

        var action = _vm.ResolveDrop(_dragId, row.Todo.Id, yRatio);
        _pendingDrop = action;
        if (action is null) { Adorner()?.Clear(); return; }

        var indent = row.Depth * 18;
        if (action.Kind == DropKind.Child)
            Adorner()?.ShowChildBox(rect.X + 2, rect.Y + 1, rect.Width - 4, rect.Height - 2);
        else if (action.Kind == DropKind.SiblingBefore)
            Adorner()?.ShowSiblingLine(rect.X + indent + 2, rect.Y, List.ActualWidth - indent - 20);
        else
            Adorner()?.ShowSiblingLine(rect.X + indent + 2, rect.Y + rect.Height, List.ActualWidth - indent - 20);
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void List_DragLeave(object sender, DragEventArgs e)
    {
        _pendingDrop = null;
        Adorner()?.Clear();
    }

    private void List_Drop(object sender, DragEventArgs e)
    {
        if (_dragId is not null && _pendingDrop is not null)
            _vm.ExecuteDrop(_dragId, _pendingDrop);
        _dragId = null;
        _pendingDrop = null;
        Adorner()?.Clear();
    }

    private (PlanRow? Row, double YRatio, Rect Rect) RowFromPoint(Point ptInList)
    {
        for (var i = 0; i < List.Items.Count; i++)
        {
            if (List.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem lbi) continue;
            var pos = lbi.TranslatePoint(new Point(0, 0), List);
            var h = lbi.ActualHeight;
            if (ptInList.Y >= pos.Y && ptInList.Y < pos.Y + h)
                return (List.Items[i] as PlanRow, (ptInList.Y - pos.Y) / (h <= 0 ? 1 : h),
                    new Rect(pos.X, pos.Y, lbi.ActualWidth, h));
        }
        return (null, 0, default);
    }

    private DropAdorner? Adorner()
    {
        if (_dropAdorner is null)
        {
            var layer = AdornerLayer.GetAdornerLayer(List);
            if (layer is null) return null;
            _dropAdorner = new DropAdorner(List);
            layer.Add(_dropAdorner);
        }
        return _dropAdorner;
    }

    // ── 右键菜单（逻辑在 VM；这里只取行与弹对话框）──

    private void MenuToggleComplete_Click(object sender, RoutedEventArgs e)
    {
        if (Row((MenuItem)sender) is { } row) _vm.ToggleCompleteCommand.Execute(row.Todo.Id);
    }

    private void MenuToggleStar_Click(object sender, RoutedEventArgs e)
    {
        if (Row((MenuItem)sender) is { } row) _vm.ToggleStarCommand.Execute(row.Todo.Id);
    }

    private void MenuRename_Click(object sender, RoutedEventArgs e)
    {
        var row = Row((MenuItem)sender);
        if (row is null) return;
        var text = InputDialog.Show(this, "重命名", "修改任务内容", row.Todo.Text);
        if (text is null) return;
        row.Todo.Text = text;
        _vm.Rebuild();
        _vm.Save();
    }

    private void MenuAddChild_Click(object sender, RoutedEventArgs e)
    {
        var row = Row((MenuItem)sender);
        if (row is null) return;
        var text = InputDialog.Show(this, "添加子任务", $"「{row.Todo.Text}」下添加子任务");
        if (text is null) return;
        _vm.AddTodo(text, row.Todo.Id, out var err);
        if (err is { Length: > 0 }) MessageBox.Show(err, "提示", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void MenuDelete_Click(object sender, RoutedEventArgs e)
    {
        var row = Row((MenuItem)sender);
        if (row is null) return;
        var (need, msg) = _vm.PeekDelete(row.Todo.Id);
        if (need && MessageBox.Show(msg, "确认删除", MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        _vm.Delete(row.Todo.Id);
    }

    private static PlanRow? Row(MenuItem mi) => mi.DataContext as PlanRow;

    // ── 计时面板 / 托盘形态 ──

    private void PanelBtn_Click(object sender, RoutedEventArgs e)
        => (Application.Current as App)?.TogglePanel();

    private void MenuPanelVisible_Click(object sender, RoutedEventArgs e)
    {
        if (Row((MenuItem)sender) is { } row) _vm.TogglePanelVisible(row.Todo.Id);
    }

    private void Card_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        if ((sender as FrameworkElement)?.DataContext is PlanRow { IsHeader: false } row)
            _vm.TogglePanelVisible(row.Todo.Id);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (Application.Current is App { Exiting: true }) return;
        e.Cancel = true;        // 关闭按钮 = 收起常驻（托盘退出真正关闭）
        Hide();
    }

    // ── 冒烟截图 ──

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
