using System.Text.Json.Serialization;

namespace TodoTimer.Models;

/// <summary>todos.json 信封。旧格式为纯数组，由 TodoStore.Load 归一（panel 100/100）。</summary>
public sealed class TodoFile
{
    [JsonPropertyName("panel_x")] public int PanelX { get; set; } = 100;
    [JsonPropertyName("panel_y")] public int PanelY { get; set; } = 100;
    [JsonPropertyName("autostart")] public bool Autostart { get; set; } = true;
    [JsonPropertyName("collapsed")] public List<string>? Collapsed { get; set; }
    [JsonPropertyName("todos")] public List<TodoItem> Todos { get; set; } = new();
}
