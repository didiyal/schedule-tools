using System.IO;
using System.Text;
using TodoTimer.Models;

namespace TodoTimer.Tests;

/// <summary>todos.json 契约对齐测试。全部用临时目录副本，绝不触碰仓库 data/。</summary>
public sealed class StoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("todotimer-test").FullName;

    private string PathOf(string name) => System.IO.Path.Combine(_dir, name);

    private void Write(string name, string content) =>
        File.WriteAllText(PathOf(name), content, new UTF8Encoding(false));

    [Fact]
    public void Load_MissingFile_ReturnsNull()
    {
        Assert.Null(TodoStore.Load(PathOf("todos.json")));
    }

    [Fact]
    public void Load_OldArrayFormat_PanelDefaults()
    {
        Write("todos.json", """[{"id":"1","text":"买牛奶","date":"2026-10-01","order":2},{"id":"2","text":"写周报"}]""");
        var file = TodoStore.Load(PathOf("todos.json"));
        Assert.NotNull(file);
        Assert.Equal(2, file.Todos.Count);
        Assert.Equal(100, file.PanelX);
        Assert.Equal(100, file.PanelY);
        Assert.True(file.Autostart);
        Assert.Equal("买牛奶", file.Todos[0].Text);
        // 缺省字段：date=今天、visible=true
        Assert.Equal(TodoItem.TodayStr(), file.Todos[1].Date);
        Assert.True(file.Todos[1].Visible);
    }

    [Fact]
    public void Load_NewDictFormat_RoundTrip()
    {
        Write("todos.json", """
        {
          "panel_x": 55, "panel_y": 77, "autostart": false,
          "collapsed": ["3"],
          "todos": [
            {"id":"1","text":"父任务","date":"2026-10-23","order":0},
            {"id":"3","text":"子任务","parent":"1","date":"2026-10-23","order":1}
          ]
        }
        """);
        var file = TodoStore.Load(PathOf("todos.json"));
        Assert.NotNull(file);
        Assert.Equal(55, file.PanelX);
        Assert.Equal(77, file.PanelY);
        Assert.False(file.Autostart);
        Assert.Equal(new List<string> { "3" }, file.Collapsed);
        Assert.Equal("1", file.Todos[1].Parent);
    }

    [Fact]
    public void Load_CorruptFile_FallsBackToBak()
    {
        Write("todos.json", "{oops not json");
        Write("todos.json.bak", """{"todos":[{"id":"9","text":"备份里的任务"}]}""");
        var file = TodoStore.Load(PathOf("todos.json"));
        Assert.NotNull(file);
        Assert.Single(file.Todos);
        Assert.Equal("备份里的任务", file.Todos[0].Text);
    }

    [Fact]
    public void Save_ThenLoad_RoundTrip_AndBakCreated()
    {
        var path = PathOf("todos.json");
        var file = new TodoFile
        {
            PanelX = 10, PanelY = 20, Autostart = false,
            Collapsed = new List<string> { "2" },
            Todos = new List<TodoItem>
            {
                new() { Id = "1", Text = "中文任务", Date = "2026-10-23" },
                new() { Id = "2", Text = "子任务", Parent = "1", Date = "2026-10-23" },
            },
        };
        TodoStore.Save(path, file);

        Assert.True(File.Exists(path + ".bak"));
        var back = TodoStore.Load(path);
        Assert.NotNull(back);
        Assert.Equal(10, back.PanelX);
        Assert.False(back.Autostart);
        Assert.Equal("1", back.Todos[1].Parent);
        Assert.Equal("中文任务", back.Todos[0].Text);
    }

    [Fact]
    public void Save_ChineseNotEscaped()
    {
        var path = PathOf("todos.json");
        TodoStore.Save(path, new TodoFile
        {
            Todos = new List<TodoItem> { new() { Id = "1", Text = "中文内容" } },
        });
        var raw = File.ReadAllText(path);
        Assert.Contains("中文内容", raw);
        Assert.DoesNotContain("\\u4e2d", raw);
    }

    [Fact]
    public void Load_NormalizesDangling_Cycle_Overdeep()
    {
        // 2 的父悬空；3↔4 成环（按 Python 语义：先遍历的 3 被拍平，4 落回顶层之下的 3）；
        // 6→7→8→9 链超深（9 违规被拍平）。期望结果与 Python 版 tree.normalize 一致。
        Write("todos.json", """
        {"todos":[
          {"id":"1","text":"top"},
          {"id":"2","text":"dangling","parent":"99"},
          {"id":"3","text":"cyc-a","parent":"4"},
          {"id":"4","text":"cyc-b","parent":"3"},
          {"id":"6","text":"l0"},
          {"id":"7","text":"l1","parent":"6"},
          {"id":"8","text":"l2","parent":"7"},
          {"id":"9","text":"l3","parent":"8"}
        ]}
        """);
        var file = TodoStore.Load(PathOf("todos.json"));
        Assert.NotNull(file);
        var parents = file.Todos.ToDictionary(t => t.Id, t => t.Parent);
        Assert.Null(parents["2"]);                       // 悬空拍平
        Assert.Null(parents["3"]);                       // 环成员（先遍历者）拍平
        Assert.Equal("3", parents["4"]);                 // 另一环成员留在拍平后的 3 下
        Assert.Null(parents["9"]);                       // 超深拍平
        Assert.Equal("6", parents["7"]);                 // 其余链不动
    }

    public void Dispose() => TryDelete();

    private void TryDelete()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 临时目录清理失败忽略 */ }
    }
}
