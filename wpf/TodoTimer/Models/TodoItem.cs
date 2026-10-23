using System.Text.Json.Serialization;

namespace TodoTimer.Models;

/// <summary>单条待办 —— 字段与 Python 版 todos.json 逐字对齐（含遗留字段 x/y）。</summary>
public sealed class TodoItem
{
    public static string TodayStr() => DateTime.Now.ToString("yyyy-MM-dd");

    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("parent")] public string? Parent { get; set; }
    [JsonPropertyName("elapsed")] public int Elapsed { get; set; }
    [JsonPropertyName("completed")] public bool Completed { get; set; }
    [JsonPropertyName("x")] public int X { get; set; }
    [JsonPropertyName("y")] public int Y { get; set; }
    [JsonPropertyName("visible")] public bool Visible { get; set; } = true;
    [JsonPropertyName("order")] public int Order { get; set; }
    [JsonPropertyName("starred")] public bool Starred { get; set; }
    [JsonPropertyName("running")] public bool Running { get; set; }
    [JsonPropertyName("last_started_at")] public int? LastStartedAt { get; set; }
    [JsonPropertyName("target_seconds")] public int? TargetSeconds { get; set; }
    [JsonPropertyName("reminded")] public bool Reminded { get; set; }
    [JsonPropertyName("date")] public string Date { get; set; } = TodayStr();
}
