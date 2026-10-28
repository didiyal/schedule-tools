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

    // ── 拖拽（手动循环：鼠标捕获 + 自家事件）──
    // 不走 OLE DoDragDrop——其 DragOver/Drop 在"让位重排"场景下语义不可控
    // （子元素 DragLeave 冒泡/Effects 决定松手/Drop 不触发），连续修了四个坑后弃用。

    private bool _dragging;
    private DropIntent _intent;      // 横轴三段当前意图(带迟滞): 提升/同级/子级

    private void List_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _downPt = e.GetPosition(List);
        var row = RowFromPoint(_downPt).Row;
        _downRowId = row is { IsHeader: false, IsPlaceholder: false, IsTodaySlot: false } ? row.Todo.Id : null;
        if (_downRowId is not null) _vm.ClearDropPreview();   // 清上一轮未落子的残留槽
        _downOnButton = WalkToButton(e.OriginalSource as DependencyObject);
    }

    private void List_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragging)
        {
            UpdateDragPoint(e.GetPosition(List));
            _ghost?.MoveToCursor();
            return;
        }
        if (e.LeftButton != MouseButtonState.Pressed || _downRowId is null) return;
        if (_downOnButton) return;
        var pt = e.GetPosition(List);
        if (Math.Abs(pt.X - _downPt.X) < 10 && Math.Abs(pt.Y - _downPt.Y) < 10) return;
        _dragging = true;
        _dragId = _downRowId;
        _intent = DropIntent.Sibling;   // 默认同级, 移动后按横轴切换
        _pendingToday = false;
        List.CaptureMouse();            // 捕获后窗口外移动/松手也送到本控件
        ShowGhost(_dragId);
        UpdateDragPoint(pt);
    }

    private void List_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        => EndDrag(apply: _pendingDrop is not null || _pendingToday);

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (_dragging && e.Key == Key.Escape) { EndDrag(apply: false); e.Handled = true; return; }
        base.OnPreviewKeyDown(e);
    }

    private bool _pendingToday;

    private void EndDrag(bool apply)
    {
        if (!_dragging) return;
        _dragging = false;
        if (apply && _dragId is not null && _pendingToday)
        {
            _vm.ResetPreviewState();
            _vm.MoveToToday(_dragId);
        }
        else         if (apply && _dragId is not null && _pendingDrop is not null)
        {
            _vm.ResetPreviewState();    // 先清预览态(不重建), ExecuteDrop 的 Rebuild 即最终态
            _vm.ExecuteDrop(_dragId, _pendingDrop);
        }
        else
        {
            _vm.ClearDropPreview();     // 取消/无效落点: 恢复原排布
        }
        _dragId = null;
        _pendingDrop = null;
        Adorner()?.Clear();
        _ghost?.Hide();
        List.ReleaseMouseCapture();
    }

    /// <summary>拖拽中按光标位置更新命中/落点/预览（原 DragOver 主体，去掉 OLE 语义）。</summary>
    private void UpdateDragPoint(Point pt)
    {
        var (row, yRatio, rect) = RowFromPoint(pt);
        if (row is null || row.IsHeader || row.IsPlaceholder) return;   // 槽/组头/空白: 保持当前状态
        // 横轴分模式(Notion 式)+迟滞: 内容区左侧=同级, 偏右=子级。
        // 独立两轴取代行高三分区, 根治同级/子级来回抖动
        var relX = pt.X - rect.X;
        _intent = _intent switch
        {
            DropIntent.Promote => relX < 30 ? DropIntent.Promote : relX > 64 ? DropIntent.Child : DropIntent.Sibling,
            DropIntent.Sibling => relX < 14 ? DropIntent.Promote : relX > 64 ? DropIntent.Child : DropIntent.Sibling,
            _ => relX < 14 ? DropIntent.Promote : relX < 52 ? DropIntent.Sibling : DropIntent.Child,
        };
        _ghost?.SetMode(_intent switch
        {
            DropIntent.Promote => "↖ 拖成主任务",
            DropIntent.Sibling => "⇅ 同级插入",
            _ => "↳ 成为子任务",
        });
        if (row.IsTodaySlot)
        {
            _pendingDrop = null;
            _pendingToday = true;      // 拖入今日空槽 = 改期到今天
            return;
        }
        _pendingToday = false;
        var action = _vm.ResolveDrop(_dragId!, row.Todo.Id, _intent, yRatio);
        _pendingDrop = action;
        // push 模式: 有效=浅蓝让位槽, 无效=红色虚线槽(反馈不能是零)
        _vm.ShowDropPreview(_dragId!, action);
        if (action is null) { Adorner()?.Clear(); return; }   // 红槽即指示, 不画线

        var indent = row.Depth * 28;
        var ad = Adorner();
        if (ad is null) return;
        ad.Show(dc =>
        {
            // 目标行 12% 暗色叠层 = 被影子压住的碰撞感(显式 alpha, 别盖住内容)
            var dim = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(30, 0, 0, 0));
            dc.DrawRoundedRectangle(dim, null,
                new System.Windows.Rect(rect.X + 1, rect.Y + 1, rect.Width - 2, rect.Height - 2),
                6, 6);

            var orange = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xE8, 0x84, 0x2C));
            if (action.Kind == DropKind.Promote)
            {
                dc.DrawRoundedRectangle(orange, null,
                    new System.Windows.Rect(rect.X, rect.Y + 2, 4, rect.Height - 4), 2, 2);
                var y = action.Before ? rect.Y : rect.Y + rect.Height;
                dc.DrawRoundedRectangle(orange, null,
                    new System.Windows.Rect(rect.X + 2, y - 2.5,
                        Math.Max(List.ActualWidth - 8, 240), 5), 2.5, 2.5);
                return;
            }
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
            // X 也必须在行内: 滚动条区域(行宽之外)点击会误命中同高度任务并起拖
            if (ptInList.X >= pos.X && ptInList.X < pos.X + lbi.ActualWidth
                && ptInList.Y >= pos.Y && ptInList.Y < pos.Y + h)
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
        (Application.Current as App)?.PanelRefreshNow();
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

    private void SyncStatus_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_vm.Sync is { } sync) SyncDialog.Show(this, sync);
    }

    private void SyncBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Sync is { } sync) SyncDialog.Show(this, sync);
    }

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
