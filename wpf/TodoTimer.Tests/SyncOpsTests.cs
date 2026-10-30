using System.Text.Json;
using TodoTimer.Models;
using Xunit;
using Remote = TodoTimer.Models.RemoteItem;

namespace TodoTimer.Tests;

/// <summary>SyncOps 纯逻辑单测（v1.5 S2 同步合并语义）。</summary>
public class SyncOpsTests
{
    private static TodoItem Todo(string id, string text = "任务", long ua = 0) => new()
    { Id = id, Text = text, UpdatedAt = ua };

    private static Remote Remote(string id, long ua, long? del = null, string? text = null)
    {
        var t = Todo(id, text ?? "远端文本", ua);
        var json = JsonSerializer.SerializeToElement(t);
        return new Remote(id, ua, del, json);
    }



    // ── 撞号回归（2026-10-30 用户报：桌面+手机同刻各造 id=213，merge 后并存两条）──

    [Fact]
    public void AddRemote_WhenDuplicateIdExists_CollapsesExtras()
    {
        var todos = new List<TodoItem>
        {
            Todo("213", "openit 管理"),
            Todo("213", "openit 管理"),   // 历史撞号产物：同 id 两条
        };
        var remote = new TodoItem { Id = "213", Text = "openit 管理", Completed = true };
        var ok = SyncOps.ApplyToList(todos,
            new Remote("213", 999, null, JsonSerializer.SerializeToElement(remote)));
        Assert.True(ok);
        Assert.Single(todos.Where(t => t.Id == "213"));          // 只剩一条
        Assert.True(todos.Single(t => t.Id == "213").Completed); // 内容=远端
    }

    [Fact]
    public void AddRemote_NewId_StillAppends()
    {
        var todos = new List<TodoItem> { Todo("1", "本地") };
        var remote = new TodoItem { Id = "2", Text = "远端新任务" };
        SyncOps.ApplyToList(todos,
            new Remote("2", 999, null, JsonSerializer.SerializeToElement(remote)));
        Assert.Equal(2, todos.Count);                            // 正常新增不受影响
    }

    // ── 指纹 ──

    [Fact]
    public void Fingerprint_SameContent_SameHash_IgnoresUpdatedAt()
    {
        var a = Todo("1", "写作业"); a.UpdatedAt = 100;
        var b = Todo("1", "写作业"); b.UpdatedAt = 999;
        Assert.Equal(SyncOps.Fingerprint(a), SyncOps.Fingerprint(b));
    }

    [Fact]
    public void Fingerprint_TextChanged_Differs()
    {
        Assert.NotEqual(SyncOps.Fingerprint(Todo("1", "A")), SyncOps.Fingerprint(Todo("1", "B")));
        var a = Todo("1", "A");
        var b = new TodoItem { Id = "1", Text = "A", Completed = true };
        Assert.NotEqual(SyncOps.Fingerprint(a), SyncOps.Fingerprint(b));
    }

    // ── BumpChanged ──

    [Fact]
    public void BumpChanged_NewAndChanged_GetNow_Unchanged_KeepsTs()
    {
        var fps = new Dictionary<string, string>();
        var old = Todo("1", "旧", ua: 500);
        fps["1"] = SyncOps.Fingerprint(old);          // 登记过、内容没变
        var now = 1000L;
        var list = new List<TodoItem> { old, Todo("2", "新"), Todo("3", "改") };
        fps["3"] = "stale-fingerprint";

        var removed = SyncOps.BumpChanged(list, fps, now);

        Assert.Equal(500, list[0].UpdatedAt);         // 没变：时间戳不动
        Assert.Equal(now, list[1].UpdatedAt);         // 新增：置 now
        Assert.Equal(now, list[2].UpdatedAt);         // 变了：置 now
        Assert.Empty(removed);
        Assert.Equal(3, fps.Count);
        // 幂等：再跑一遍时间戳不再变
        SyncOps.BumpChanged(list, fps, now + 1);
        Assert.Equal(now, list[1].UpdatedAt);
        Assert.Equal(now, list[2].UpdatedAt);
    }

    [Fact]
    public void BumpChanged_RemovedIds_Returned_AndFingerprintCleaned()
    {
        var fps = new Dictionary<string, string>
        {
            ["1"] = "x", ["2"] = "y", ["9"] = "gone",
        };
        var list = new List<TodoItem> { Todo("1"), Todo("2") };
        var removed = SyncOps.BumpChanged(list, fps, 100);
        Assert.Equal(["9"], removed);
        Assert.False(fps.ContainsKey("9"));
    }

    // ── Decide / ApplyToList ──

    [Fact]
    public void Apply_AddRemote_NewItemAppended()
    {
        var todos = new List<TodoItem>();
        Assert.True(SyncOps.ApplyToList(todos, Remote("5", 100, text: "远端新增")));
        var t = Assert.Single(todos);
        Assert.Equal("远端新增", t.Text);
        Assert.Equal(100, t.UpdatedAt);
    }

    [Fact]
    public void Apply_TakeRemote_NewerWins()
    {
        var todos = new List<TodoItem> { Todo("1", "本地旧", ua: 100) };
        Assert.True(SyncOps.ApplyToList(todos, Remote("1", 200, text: "远端新")));
        Assert.Equal("远端新", Assert.Single(todos).Text);
    }

    [Fact]
    public void Apply_KeepLocal_OlderRemoteIgnored()
    {
        var todos = new List<TodoItem> { Todo("1", "本地新", ua: 300) };
        Assert.False(SyncOps.ApplyToList(todos, Remote("1", 200, text: "远端旧")));
        Assert.Equal("本地新", Assert.Single(todos).Text);
    }

    [Fact]
    public void Apply_DeleteLocal_TombstoneRemoves()
    {
        var todos = new List<TodoItem> { Todo("1", "将被删", ua: 100) };
        Assert.True(SyncOps.ApplyToList(todos, Remote("1", 200, del: 200, text: "x")));
        Assert.Empty(todos);
    }

    [Fact]
    public void Apply_DeleteRemote_UnknownId_Noop()
    {
        var todos = new List<TodoItem> { Todo("1") };
        Assert.False(SyncOps.ApplyToList(todos, Remote("99", 200, del: 200)));
        Assert.Single(todos);
    }

    [Fact]
    public void Apply_SameTimestamp_TieBreakLocal()
    {
        var todos = new List<TodoItem> { Todo("1", "本地", ua: 100) };
        Assert.False(SyncOps.ApplyToList(todos, Remote("1", 100, text: "远端")));
        Assert.Equal("本地", Assert.Single(todos).Text);   // 平局保留本地（权威端语义）
    }

    [Fact]
    public void Apply_BadJson_Dropped()
    {
        var bad = new Remote("1", 999, null, JsonDocument.Parse("{\"id\":123}").RootElement.Clone());
        var todos = new List<TodoItem>();
        Assert.False(SyncOps.ApplyToList(todos, bad));   // 缺 text 字段的反序列化不应崩溃或产生半成品
        Assert.True(todos.Count is 0 or 1);              // 要么丢弃要么安全容纳，不抛异常
    }

    // ── 双向往返：本地改→上行→另一端应用 的最小闭环 ──

    [Fact]
    public void RoundTrip_LocalEdit_ReachesOtherSide()
    {
        var fpsA = new Dictionary<string, string>();
        var a = new List<TodoItem> { Todo("1", "A端初版") };
        SyncOps.BumpChanged(a, fpsA, 100);

        // B 端初始同步拿到同一条（模拟已一致）
        var fpsB = new Dictionary<string, string> { ["1"] = fpsA["1"] };
        var b = new List<TodoItem> { Todo("1", "A端初版") };

        // A 端改文本 → bump → 上行批次含该条
        a[0].Text = "A端改了";
        var removedA = SyncOps.BumpChanged(a, fpsA, 200);
        Assert.Empty(removedA);
        Assert.Equal(200, a[0].UpdatedAt);

        // B 端同时没改这条 → 应用 A 的上行 → 采纳
        var remote = new Remote("1", a[0].UpdatedAt, null,
            JsonSerializer.SerializeToElement(a[0]));
        Assert.True(SyncOps.ApplyToList(b, remote));
        Assert.Equal("A端改了", b[0].Text);
    }

    [Fact]
    public void RoundTrip_DeleteOnA_RemovesOnB()
    {
        var fpsA = new Dictionary<string, string> { ["1"] = "f" };
        var a = new List<TodoItem>();                 // A 端已删
        var removed = SyncOps.BumpChanged(a, fpsA, 300);
        Assert.Equal(["1"], removed);

        var b = new List<TodoItem> { Todo("1", "B端还在", ua: 100) };
        Assert.True(SyncOps.ApplyToList(b, new Remote("1", 300, 300,
            JsonSerializer.SerializeToElement(Todo("1")))));
        Assert.Empty(b);
    }
}
