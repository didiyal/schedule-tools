using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Media;

namespace TodoTimer.Views;

/// <summary>拖拽影子：跟随光标的半透明卡片，给拖拽"物理体积"。</summary>
public sealed class DragGhostWindow : Window
{
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT p);

    private struct POINT { public int X; public int Y; }

    private readonly TextBlock _text = new()
    {
        FontSize = 12,
        Foreground = Brushes.White,
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly Border _badge = new()
    {
        Background = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(6, 1, 6, 1),
        Margin = new Thickness(6, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Visibility = Visibility.Collapsed,
    };

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Win32 级鼠标穿透：WS_EX_TRANSPARENT|WS_EX_LAYERED。
        // 影子 Topmost 跟随光标, 不穿透会抢走 OLE 拖拽的落点目标( DragOver/Drop 全丢)
        if (PresentationSource.FromVisual(this) is HwndSource src)
        {
            var style = GetWindowLong(src.Handle, GWL_EXSTYLE);
            SetWindowLong(src.Handle, GWL_EXSTYLE,
                style | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE);
        }
    }

    public DragGhostWindow()
    {
        var badgeText = new TextBlock { FontSize = 10, Foreground = Brushes.White };
        _badge.Child = badgeText;
        var dock = new DockPanel();
        DockPanel.SetDock(_badge, Dock.Right);
        dock.Children.Add(_badge);
        dock.Children.Add(_text);
        var host = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(210, 0x33, 0x70, 0xFF)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8, 12, 8),
            Child = dock,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            { BlurRadius = 14, ShadowDepth = 3, Opacity = 0.4 },
        };
        Content = host;
        Width = 190;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        IsHitTestVisible = false;
    }

    public void Show(string text, int subtreeExtra)
    {
        _text.Text = text;
        if (subtreeExtra > 0)
        {
            _badge.Visibility = Visibility.Visible;
            _badge.Child = new TextBlock
            { FontSize = 10, Foreground = Brushes.White, Text = $"+{subtreeExtra} 项" };
        }
        else _badge.Visibility = Visibility.Collapsed;
        MoveToCursor();
        Show();
    }

    public void MoveToCursor()
    {
        if (!GetCursorPos(out var p)) return;
        // GetCursorPos 是物理像素, WPF Left/Top 是 DIP——高 DPI 下不换算会出现大高低差
        var dpiX = 1.0; var dpiY = 1.0;
        if (PresentationSource.FromVisual(this)?.CompositionTarget is { } ct)
        {
            dpiX = ct.TransformToDevice.M11;
            dpiY = ct.TransformToDevice.M22;
        }
        Left = p.X / dpiX - ActualWidth / 2;
        Top = p.Y / dpiY - ActualHeight / 2;
    }
}
