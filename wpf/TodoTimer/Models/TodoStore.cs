using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace TodoTimer.Models;

/// <summary>
/// todos.json 读写，契约对齐 Python 版（workspace/src/main_app.py）：
/// 旧数组格式兼容（panel 100/100）；坏文件回退 .bak；
/// 原子写 .tmp → replace 主文件 → 复制出 .bak（备份失败忽略）；
/// 装载即 normalize 自愈（悬空/成环/超深 → 拍平顶层）。
/// </summary>
public static class TodoStore
{
    /// <summary>落盘格式（缩进+中文不转义）——todos.json/state 文件共用，勿另建副本。</summary>
    public static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    /// <summary>紧凑传输格式（同步上行 payload 用）。</summary>
    public static readonly JsonSerializerOptions CompactOpts = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private static readonly JsonSerializerOptions ReadOpts = new();

    public static TodoFile? Load(string path)
    {
        if (!File.Exists(path)) return null;
        JsonDocument? doc = TryParse(path);
        if (doc is null)
        {
            var bak = path + ".bak";
            doc = File.Exists(bak) ? TryParse(bak) : null;
            if (doc is null) return null;
        }

        using (doc)
        {
            var file = doc.RootElement.ValueKind == JsonValueKind.Array
                ? new TodoFile
                {
                    PanelX = 100,
                    PanelY = 100,
                    Todos = doc.RootElement.Deserialize<List<TodoItem>>(ReadOpts) ?? new(),
                }
                : doc.RootElement.Deserialize<TodoFile>(ReadOpts) ?? new TodoFile();

            // 装载自愈：只影响内存态 parent 引用
            var parents = new Dictionary<string, string?>();
            foreach (var t in file.Todos) parents[t.Id] = t.Parent;
            var healed = TaskTree.Normalize(parents);
            foreach (var t in file.Todos) t.Parent = healed[t.Id];
            return file;
        }
    }

    public static void Save(string path, TodoFile data)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(data, WriteOpts), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
        try { File.Copy(path, path + ".bak", overwrite: true); }
        catch { /* 同 Python 版：滚动备份失败不阻断 */ }
    }

    private static JsonDocument? TryParse(string file)
    {
        try { return JsonDocument.Parse(File.ReadAllText(file)); }
        catch { return null; }
    }

    /// <summary>archive.json 是纯数组（对齐 Python 版），坏文件按空数组处理。</summary>
    public static List<TodoItem> LoadArchive(string path)
    {
        if (!File.Exists(path)) return new List<TodoItem>();
        try
        {
            return JsonSerializer.Deserialize<List<TodoItem>>(File.ReadAllText(path), ReadOpts) ?? new();
        }
        catch { return new List<TodoItem>(); }
    }

    public static void SaveArchive(string path, List<TodoItem> archive)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(archive, WriteOpts), new UTF8Encoding(false));
    }
}
