using System.Runtime.InteropServices;
using System.Windows;
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
        Width = 240;
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
        if (GetCursorPos(out var p))
        {
            Left = p.X + 10;
            Top = p.Y - 16;
        }
    }
}
