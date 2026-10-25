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
    private bool _downOnButton;   // 从按钮(折叠箭头等)上起的按下不进入拖拽，防误触
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
        _downOnButton = WalkToButton(e.OriginalSource as DependencyObject);
    }

    private void List_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _downRowId is null || _dragId is not null)
            return;
        if (_downOnButton) return;
        var pt = e.GetPosition(List);
        if (Math.Abs(pt.X - _downPt.X) < 10 && Math.Abs(pt.Y - _downPt.Y) < 10) return;
        _dragId = _downRowId;
        ShowGhost(_dragId);
        DragDrop.DoDragDrop(List, new DataObject(DataFormats.StringFormat, _dragId), DragDropEffects.Move);
        _ghost?.Hide();
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
        // 空白区/组头/占位槽上：保持当前让位状态。
        // 占位槽是空 Todo, 若当目标解析必然失败并清掉预览 => 槽位闪烁、永远推不开
        if (row is null || row.IsHeader || row.IsPlaceholder) return;

        var action = _vm.ResolveDrop(_dragId, row.Todo.Id, yRatio);
        _pendingDrop = action;
        if (action is null) { Adorner()?.Clear(); _vm.ClearDropPreview(); return; }
        // push 模式: 空白槽位即指示器(其他卡片推开让位), 不再画线
        _vm.ShowDropPreview(_dragId, action);

        var indent = row.Depth * 28;
        var ad = Adorner();
        if (ad is null) return;
        ad.Show(dc =>
        {
            // 目标行暗色投影 = 被影子压住的碰撞感
            dc.DrawRoundedRectangle(
                System.Windows.Media.Brushes.Black, null,
                new System.Windows.Rect(rect.X + 1, rect.Y + 1, rect.Width - 2, rect.Height - 2),
                6, 6);
            dc.PushOpacity(0.12);
            dc.DrawRoundedRectangle(
                System.Windows.Media.Brushes.White, null,
                new System.Windows.Rect(rect.X + 1, rect.Y + 1, rect.Width - 2, rect.Height - 2),
                6, 6);
            dc.Pop();

            var green = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x2B, 0xA2, 0x45));
            var blue = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x1F, 0x6B, 0xFF));
            if (action.Kind == DropKind.Child)
            {
                dc.DrawRoundedRectangle(null, new System.Windows.Media.Pen(green, 3),
                    new System.Windows.Rect(rect.X + 2, rect.Y + 1, rect.Width - 4, rect.Height - 2), 6, 6);
                dc.DrawRoundedRectangle(green, null,
                    new System.Windows.Rect(rect.X + 28, rect.Y + rect.Height - 2.5,
                        Math.Max(rect.Width - 28, 220), 5), 2.5, 2.5);
            }
            else
            {
                var y = action.Kind == DropKind.SiblingBefore ? rect.Y : rect.Y + rect.Height;
                dc.DrawRoundedRectangle(blue, null,
                    new System.Windows.Rect(rect.X + indent + 2, y - 2.5,
                        Math.Max(List.ActualWidth - indent - 20, 240), 5), 2.5, 2.5);
                dc.DrawEllipse(blue, null,
                    new System.Windows.Point(rect.X + indent + 5, y), 6, 6);
            }
        });
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void List_DragLeave(object sender, DragEventArgs e)
    {
        _pendingDrop = null;
        Adorner()?.Clear();
        _vm.ClearDropPreview();
    }

    private void List_Drop(object sender, DragEventArgs e)
    {
        if (_dragId is not null && _pendingDrop is not null)
            _vm.ExecuteDrop(_dragId, _pendingDrop);
        _dragId = null;
        _pendingDrop = null;
        Adorner()?.Clear();
        _vm.ClearDropPreview();
    }

    private static bool WalkToButton(DependencyObject? o)
    {
        while (o is not null and not ListBox)
        {
            if (o is System.Windows.Controls.Primitives.ButtonBase) return true;
            o = System.Windows.Media.VisualTreeHelper.GetParent(o);
        }
        return false;
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

    private DragGhostWindow? _ghost;

    private void ShowGhost(string id)
    {
        if (_vm.GetTodo(id) is not { } t) return;
        _ghost ??= new DragGhostWindow();
        _ghost.Show(t.Text, _vm.SubtreeExtraCount(id));
    }

    private void List_QueryContinueDrag(object sender, QueryContinueDragEventArgs e)
    {
        if (e.EscapePressed) e.Action = DragAction.Cancel;   // Esc 取消拖拽
        _ghost?.MoveToCursor();
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
