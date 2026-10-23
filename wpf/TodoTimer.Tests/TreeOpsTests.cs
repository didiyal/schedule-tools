using System.IO;
using TodoTimer.Models;
using TodoTimer.ViewModels;

namespace TodoTimer.Tests;

/// <summary>拖拽落点解析纯函数测试 —— 三区/同父/同日期/成环/超深，对照 Python _resolve_drop。</summary>
public sealed class DropResolverTests
{
    private const string Today = "2026-10-23";

    private static Dictionary<string, TodoItem> MakeItems(params TodoItem[] items)
        => items.ToDictionary(t => t.Id);

    private static Dictionary<string, string?> ParentsOf(Dictionary<string, TodoItem> byId)
        => byId.ToDictionary(kv => kv.Key, kv => kv.Value.Parent);

    [Fact]
    public void Sibling_Zones_RequireSameParent()
    {
        var byId = MakeItems(
            new TodoItem { Id = "a", Date = Today },
            new TodoItem { Id = "b", Date = Today },
            new TodoItem { Id = "c", Date = Today, Parent = "a" });
        var parents = ParentsOf(byId);

        // a→b：同为顶层同日期，上下 25% 兄弟插入合法
        Assert.Equal(DropKind.SiblingBefore,
            DropResolver.Resolve(parents, byId, "a", "b", 0.1)?.Kind);
        Assert.Equal(DropKind.SiblingAfter,
            DropResolver.Resolve(parents, byId, "a", "b", 0.9)?.Kind);
        // a→c：父不同，兄弟区非法
        Assert.Null(DropResolver.Resolve(parents, byId, "a", "c", 0.1));
        // 顶层跨日期：兄弟区非法
        var d = new TodoItem { Id = "d", Date = "2026-10-01" };
        byId["d"] = d;
        Assert.Null(DropResolver.Resolve(ParentsOf(byId), byId, "a", "d", 0.1));
    }

    [Fact]
    public void Middle_Zone_BecomesChild_WithGuards()
    {
        var byId = MakeItems(
            new TodoItem { Id = "p", Date = Today },
            new TodoItem { Id = "q", Date = Today },
            new TodoItem { Id = "x", Date = Today, Parent = "p" },
            new TodoItem { Id = "y", Date = Today, Parent = "x" });
        var parents = ParentsOf(byId);

        Assert.Equal(DropKind.Child, DropResolver.Resolve(parents, byId, "q", "p", 0.5)?.Kind);
        // p 的子树已 2 层，再收 q 为 p 的子不超限？p(x(y))：x depth1, y depth2 → p 收 q 后 q depth1 ✓
        Assert.Equal(DropKind.Child, DropResolver.Resolve(parents, byId, "q", "x", 0.5)?.Kind);
        // 成环：p 拖到自己的后代 x 上（中间区）
        Assert.Null(DropResolver.Resolve(parents, byId, "p", "x", 0.5));
        // 自拖到自身
        Assert.Null(DropResolver.Resolve(parents, byId, "p", "p", 0.5));
        // 超深：构造 3 层链 root(p(x(y)))，把 y 挂到最深处再拖 x 到 y —— y 收 x 会到 4 层
        var deep = MakeItems(
            new TodoItem { Id = "l0", Date = Today },
            new TodoItem { Id = "l1", Date = Today, Parent = "l0" },
            new TodoItem { Id = "l2", Date = Today, Parent = "l1" });
        Assert.Null(DropResolver.Resolve(ParentsOf(deep), deep, "l0", "l2", 0.5));
    }

    [Fact]
    public void Self_Drop_AlwaysRejected()
    {
        var byId = MakeItems(new TodoItem { Id = "a", Date = Today });
        var parents = ParentsOf(byId);
        Assert.Null(DropResolver.Resolve(parents, byId, "a", "a", 0.1));
        Assert.Null(DropResolver.Resolve(parents, byId, "a", "a", 0.5));
        Assert.Null(DropResolver.Resolve(parents, byId, "a", "a", 0.9));
    }
}

/// <summary>树操作集成测试 —— 走真实 MainViewModel（临时目录），对照 Python 语义。</summary>
public sealed class TreeOpsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("todotimer-ops").FullName;

    private MainViewModel NewVm(params TodoItem[] seed)
    {
        TodoStore.Save(Path.Combine(_dir, "todos.json"), new TodoFile
        { Todos = new List<TodoItem>(seed) });
        var vm = new MainViewModel(_dir);
        vm.LoadFile();
        return vm;
    }

    private static TodoItem T(string id, string text = "任务", string? parent = null,
        int order = 0, bool completed = false, string? date = null, int elapsed = 0)
        => new()
        { Id = id, Text = text, Parent = parent, Order = order, Completed = completed,
          Date = date ?? "2026-10-23", Elapsed = elapsed };

    [Fact]
    public void AddChild_InheritsParentDate_GuardDepthCap()
    {
        var vm = NewVm(T("p", date: "2026-10-01"), T("c", parent: "p", order: 1));
        var id = vm.AddTodo("孙", "c", out var err);
        Assert.Null(err);
        var todos = vm_DebugTodos(vm);
        Assert.Equal(todos["c"].Date, todos[id!].Date);   // 子继承直接父任务的日期
        // 3 层已满：再往下加被拒
        var id2 = vm.AddTodo("曾孙", id!, out var err2);
        Assert.Null(id2);
        Assert.Equal("已达最大层级（3 层）", err2);
    }

    [Fact]
    public void Indent_AppendsAsLastChildOfPrevSibling()
    {
        var vm = NewVm(T("a", order: 1), T("b", order: 2), T("a1", parent: "a", order: 3));
        Assert.True(vm.Indent("b"));
        var todos = vm_DebugTodos(vm);
        Assert.Equal("a", todos["b"].Parent);
        Assert.True(todos["b"].Order > todos["a1"].Order);   // 追加为 a 的最后一个子
        // 首兄弟无上一兄弟：拒绝
        Assert.False(vm.Indent("a"));
    }

    [Fact]
    public void Outdent_Midpoint_WhenGapAllows()
    {
        // 结构: r → p, next(order 100)；p → w。w 提升后应插在 p(10) 与 next(100) 中点
        var vm = NewVm(
            T("r"), T("p", parent: "r", order: 10), T("next", parent: "r", order: 100),
            T("w", parent: "p", order: 1));
        Assert.True(vm.Outdent("w"));
        var todos = vm_DebugTodos(vm);
        Assert.Equal("r", todos["w"].Parent);            // 提升到祖父 r 下
        Assert.Equal(55, todos["w"].Order);   // (10+100)/2
    }

    [Fact]
    public void Outdent_NoGap_RenumberFollowingSiblings()
    {
        // r → p(1), n2(2), n3(3)；p → w。w 提升：紧跟 p(order 2)，n2/n3 顺移
        var vm = NewVm(
            T("r"), T("p", parent: "r", order: 1), T("n2", parent: "r", order: 2),
            T("n3", parent: "r", order: 3), T("w", parent: "p", order: 9));
        Assert.True(vm.Outdent("w"));
        var todos = vm_DebugTodos(vm);
        Assert.Equal("r", todos["w"].Parent);            // 提升到祖父 r 下
        Assert.Equal(2, todos["w"].Order);
        Assert.Equal(3, todos["n2"].Order);
        Assert.Equal(4, todos["n3"].Order);
    }

    [Fact]
    public void DeleteUndo_RestoresBranchWithSameIds_ParentsFirst()
    {
        var vm = NewVm(
            T("p", elapsed: 120), T("c1", parent: "p", order: 1),
            T("g1", parent: "c1", order: 2), T("keep", order: 3));
        var (need, msg) = vm.PeekDelete("p");
        Assert.True(need);
        Assert.Contains("2 个子任务", msg);
        Assert.Contains("2分", msg);

        vm.Delete("p");
        Assert.Equal(1, vm_DebugTodos(vm).Count);
        Assert.True(vm.Undo());
        var todos = vm_DebugTodos(vm);
        Assert.Equal(4, todos.Count);
        Assert.Equal("p", todos["c1"].Parent);           // 整枝引用原样恢复
        Assert.Equal("c1", todos["g1"].Parent);
        Assert.Equal(120, todos["p"].Elapsed);
    }

    [Fact]
    public void Archive_LiftsUncompletedDescendants_ToNearestUnarchivedAncestor()
    {
        // p(已完成) → w(未完成)；p 同时有已完成子 c
        var vm = NewVm(
            T("p", completed: true), T("c", parent: "p", order: 1, completed: true),
            T("w", parent: "p", order: 2));
        Assert.Equal(2, vm.CompletedCount());
        var archived = vm.ArchiveCompleted();
        Assert.Equal(2, archived);
        var todos = vm_DebugTodos(vm);
        Assert.Single(todos);                            // p、c 已归档
        Assert.Null(todos["w"].Parent);                  // 未完成后代上抛到顶层
        var archive = TodoStore.LoadArchive(Path.Combine(_dir, "archive.json"));
        Assert.Equal(2, archive.Count);
        Assert.All(archive, t => Assert.NotNull(t.ArchivedAt));
    }

    [Fact]
    public void ExecuteDrop_Child_AppendsLast_AndSibling_Renumbers()
    {
        var vm = NewVm(
            T("a", order: 1), T("b", order: 2), T("c", order: 3),
            T("a1", parent: "a", order: 4));
        // b 拖到 a 中间区：成为 a 的子、排最后
        Assert.True(vm.ExecuteDrop("b", new DropAction(DropKind.Child, "a")));
        var todos = vm_DebugTodos(vm);
        Assert.Equal("a", todos["b"].Parent);
        Assert.True(todos["b"].Order > todos["a1"].Order);

        // c 拖到 a 上方 25%：兄弟插入到 a 前（顶层同日期）
        Assert.True(vm.ExecuteDrop("c", new DropAction(DropKind.SiblingBefore, "a")));
        todos = vm_DebugTodos(vm);
        Assert.Null(todos["c"].Parent);
        // a、c、b 的相对视觉序：c 在 a 前 → c.order < a.order（b 是 a 的子不参与）
        Assert.True(todos["c"].Order < todos["a"].Order);
    }

    [Fact]
    public void DropResolver_ViaVm_RejectsCrossDateSibling()
    {
        var vm = NewVm(T("a", date: "2026-10-23"), T("b", date: "2026-10-01"));
        Assert.Null(vm.ResolveDrop("a", "b", 0.1));      // 顶层跨日期兄弟插入拒绝
        Assert.NotNull(vm.ResolveDrop("a", "b", 0.5));   // 中间区：b 收 a 为子，合法
    }

    private static Dictionary<string, TodoItem> vm_DebugTodos(MainViewModel vm)
        => vm.Rows.Where(r => !r.IsHeader).ToDictionary(r => r.Todo.Id, r => r.Todo);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
