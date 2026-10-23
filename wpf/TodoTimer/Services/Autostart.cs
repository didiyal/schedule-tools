using Microsoft.Win32;
using System.Reflection;

namespace TodoTimer.Services;

/// <summary>
/// 开机自启：HKCU Run 键，值名 "TodoTimer"（与 Python 版同名同键，沿用手动语义）。
/// </summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "TodoTimer";

    public static bool IsEnabled()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(AppName) is not null;
        }
        catch { return false; }
    }

    public static bool Set(bool enabled)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (k is null) return false;
            if (enabled)
            {
                var exe = Environment.ProcessPath
                    ?? Assembly.GetExecutingAssembly().Location;
                k.SetValue(AppName, $"\"{exe}\"");
            }
            else
            {
                k.DeleteValue(AppName, throwOnMissingValue: false);
            }
            return true;
        }
        catch { return false; }
    }
}
