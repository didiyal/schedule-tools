using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TodoTimer.Views;

/// <summary>极简单输入对话框（添加子任务 / 重命名共用）。返回 null = 取消/空文本。</summary>
public static class InputDialog
{
    public static string? Show(Window owner, string title, string prompt, string initial = "")
    {
        var box = new TextBox { Text = initial, FontSize = 13, Margin = new Thickness(0, 0, 0, 2) };
        var ok = new Button
        {
            Content = "保存", Width = 76, Height = 30, IsDefault = true,
            Style = (Style)Application.Current.Resources["BtnPrimary"],
        };
        var cancel = new Button
        {
            Content = "取消", Width = 76, Height = 30, IsCancel = true,
            Style = (Style)Application.Current.Resources["BtnSecondary"],
            Margin = new Thickness(8, 0, 0, 0),
        };

        var panel = new StackPanel { Margin = new Thickness(18, 16, 18, 16) };
        panel.Children.Add(new TextBlock
        {
            Text = prompt,
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x23, 0x29)),
            Margin = new Thickness(0, 0, 0, 10),
        });
        panel.Children.Add(box);
        var btns = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        btns.Children.Add(ok);
        btns.Children.Add(cancel);
        panel.Children.Add(btns);

        Window? win = null;
        void Accept()
        {
            win!.DialogResult = true;
            win.Close();
        }
        ok.Click += (_, _) => Accept();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Accept(); };

        win = new Window
        {
            Title = title,
            Owner = owner,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Background = Brushes.White,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            Content = panel,
        };
        box.Focus();
        box.SelectAll();
        return win.ShowDialog() == true ? box.Text.Trim() is { Length: > 0 } s ? s : null : null;
    }
}
