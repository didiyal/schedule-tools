namespace TodoTimer.Models;

/// <summary>应用版本单点——标题/更新检查共用（服务端 healthz 的 app 字段与此比对）。</summary>
public static class AppInfo
{
    public const string Version = "1.4.0";
    public const string WindowTitle = "待办计时器 1.4";
}
