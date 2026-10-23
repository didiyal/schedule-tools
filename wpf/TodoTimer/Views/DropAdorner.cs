using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

/// <summary>拖拽落点指示器：兄弟=蓝线（与目标同缩进），子级=目标绿框。</summary>
public sealed class DropAdorner : Adorner
{
    private readonly DrawingVisual _dv = new();

    public DropAdorner(UIElement adorned) : base(adorned)
    {
        AddVisualChild(_dv);
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _dv;

    public void Clear()
    {
        _dv.RenderOpen().Close();
    }

    /// <summary>蓝色插入线（兄弟插入位置）。</summary>
    public void ShowSiblingLine(double x, double y, double width)
    {
        using var dc = _dv.RenderOpen();
        dc.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromRgb(0x33, 0x70, 0xFF)), null,
            new Rect(x, y - 1.5, Math.Max(width, 80), 3), 1.5, 1.5);
    }

    /// <summary>绿色边框（成为子任务的目标框）+ 底边绿色插入线。</summary>
    public void ShowChildBox(double x, double y, double width, double height)
    {
        using var dc = _dv.RenderOpen();
        var green = new SolidColorBrush(Color.FromRgb(0x2B, 0xA2, 0x45));
        var pen = new Pen(green, 2);
        dc.DrawRoundedRectangle(null, pen, new Rect(x, y, width, height), 6, 6);
        dc.DrawRoundedRectangle(green, null,
            new Rect(x + 18, y + height - 1.5, Math.Max(width - 18, 60), 3), 1.5, 1.5);
    }
}
