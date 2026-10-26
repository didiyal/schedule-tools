using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TodoTimer.Models;
using TodoTimer.Services;

namespace TodoTimer.Views;

/// <summary>云同步设置对话框（v1.5 S2）：服务器地址 + 管理员令牌，保存前先连接探测并钉证书。</summary>
public static class SyncDialog
{
    public static void Show(Window owner, SyncService sync)
    {
        var st = sync.SnapshotState();
        var server = new TextBox { Text = st.Server, FontSize = 13 };
        var token = new TextBox
        {
            Text = st.AdminToken,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = false,
        };
        var hint = new TextBlock
        {
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x91, 0x9F)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
            Text = "服务器即部署地址（如 https://47.112.114.139）；令牌为服务器 data/admin.token 的内容" +
                   "（桌面端=权威端，手机经配对码接入）。首次保存将锁定服务器证书指纹。",
        };
        var ok = new Button
        {
            Content = "连接并保存", Width = 96, Height = 30, IsDefault = true,
            Style = (Style)Application.Current.Resources["BtnPrimary"],
        };
        var disconnect = new Button
        {
            Content = "断开", Width = 64, Height = 30,
            Style = (Style)Application.Current.Resources["BtnSecondary"],
            IsEnabled = st.Enabled,
        };
        var cancel = new Button
        {
            Content = "关闭", Width = 64, Height = 30, IsCancel = true,
            Style = (Style)Application.Current.Resources["BtnSecondary"],
            Margin = new Thickness(8, 0, 0, 0),
        };
        var msg = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = Visibility.Collapsed,
        };

        var panel = new StackPanel { Margin = new Thickness(18, 16, 18, 16) };
        panel.Children.Add(new TextBlock
        {
            Text = "云同步",
            FontSize = 14, FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x23, 0x29)),
            Margin = new Thickness(0, 0, 0, 10),
        });
        panel.Children.Add(new TextBlock { Text = "服务器地址", FontSize = 12, Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(server);
        panel.Children.Add(new TextBlock { Text = "管理员令牌", FontSize = 12, Margin = new Thickness(0, 10, 0, 4) });
        panel.Children.Add(token);
        panel.Children.Add(hint);
        panel.Children.Add(msg);
        var btns = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        btns.Children.Add(disconnect);
        btns.Children.Add(ok);
        btns.Children.Add(cancel);
        panel.Children.Add(btns);

        ok.Click += async (_, _) =>
        {
            ok.IsEnabled = false;
            msg.Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x91, 0x9F));
            msg.Visibility = Visibility.Visible;
            msg.Text = "连接中…";
            var err = await sync.ConfigureAsync(server.Text, token.Text);
            ok.IsEnabled = true;
            if (err is not null)
            {
                msg.Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D));
                msg.Text = err;
                return;
            }
            msg.Foreground = new SolidColorBrush(Color.FromRgb(0x2B, 0xA2, 0x45));
            msg.Text = "已连接，同步已开启（证书指纹已锁定）。";
            disconnect.IsEnabled = true;
        };
        disconnect.Click += (_, _) =>
        {
            sync.ClearConfig();
            disconnect.IsEnabled = false;
            msg.Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x91, 0x9F));
            msg.Visibility = Visibility.Visible;
            msg.Text = "已断开并清除本地同步状态。";
        };

        var win = new Window
        {
            Title = "云同步设置",
            Owner = owner,
            Width = 430,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Background = Brushes.White,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            Content = panel,
        };
        win.ShowDialog();
    }
}
