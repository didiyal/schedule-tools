using TodoTimer.Models;

namespace TodoTimer.Tests;

/// <summary>TaskTree 纯函数测试 —— 断言语义对照 Python 版 build_tmp 冒烟 + tree.py 注释。</summary>
public sealed class TaskTreeTests
{
    [Fact]
    public void DepthOf_CountsLevels()
    {
        var parents = new Dictionary<string, string?> { ["a"] = null, ["b"] = "a", ["c"] = "b" };
        Assert.Equal(0, TaskTree.DepthOf(parents, "a"));
        Assert.Equal(1, TaskTree.DepthOf(parents, "b"));
        Assert.Equal(2, TaskTree.DepthOf(parents, "c"));
        Assert.Equal(0, TaskTree.DepthOf(parents, "missing"));
    }

    [Fact]
    public void SubtreeHeight_SingleNodeIsOne()
    {
        var parents = new Dictionary<string, string?> { ["a"] = null, ["b"] = "a", ["c"] = "b" };
        Assert.Equal(1, TaskTree.SubtreeHeight(parents, "c"));
        Assert.Equal(3, TaskTree.SubtreeHeight(parents, "a"));
    }

    [Fact]
    public void CanAdopt_RejectsSelf_Descendant_Overdeep()
    {
        // a → b → c 链，另有顶层 t
        var parents = new Dictionary<string, string?>
        { ["a"] = null, ["b"] = "a", ["c"] = "b", ["t"] = null };

        Assert.False(TaskTree.CanAdopt(parents, "b", "b"));          // 自己
        Assert.False(TaskTree.CanAdopt(parents, "b", "a"));          // 后代
        Assert.False(TaskTree.CanAdopt(parents, "t", "a"));          // a 子树 3 层挂到 depth1 → 4 层超限
        Assert.True(TaskTree.CanAdopt(parents, "a", "t"));           // 顶层单节点挂 a 下 OK
        Assert.True(TaskTree.CanAdopt(parents, null, "b"));          // 提升为顶层 OK
        Assert.False(TaskTree.CanAdopt(parents, "missing", "t"));    // 父不存在
    }

    [Fact]
    public void Normalize_FlattensDangling_AndCycle()
    {
        var parents = new Dictionary<string, string?>
        { ["1"] = null, ["2"] = "99", ["3"] = "4", ["4"] = "3" };
        var fixedMap = TaskTree.Normalize(parents);
        Assert.Null(fixedMap["2"]);
        // Python 语义：按插入序先遍历的环成员拍平，另一成员留在其下（不再是环）
        Assert.Null(fixedMap["3"]);
        Assert.Equal("3", fixedMap["4"]);
        Assert.Equal(1, TaskTree.DepthOf(fixedMap, "4"));
    }

    [Fact]
    public void Normalize_FlattensOnlyShallowestOverdeepLayer()
    {
        // 6→7→8→9：9 超深被拍平，7/8 保留
        var parents = new Dictionary<string, string?>
        { ["6"] = null, ["7"] = "6", ["8"] = "7", ["9"] = "8" };
        var fixedMap = TaskTree.Normalize(parents);
        Assert.Equal("6", fixedMap["7"]);
        Assert.Equal("7", fixedMap["8"]);
        Assert.Null(fixedMap["9"]);
    }

    [Fact]
    public void Normalize_NoopForValidTree()
    {
        var parents = new Dictionary<string, string?>
        { ["a"] = null, ["b"] = "a", ["c"] = "b" };
        var fixedMap = TaskTree.Normalize(parents);
        Assert.Equal(parents, fixedMap);
    }

    [Fact]
    public void DfsOrder_ParentBeforeChildren()
    {
        var children = new Dictionary<string, List<string>>
        {
            ["a"] = new() { "b", "e" },
            ["b"] = new() { "c", "d" },
            ["z"] = new(),
        };
        var order = TaskTree.DfsOrder(children, new[] { "z", "a" });
        Assert.Equal(new[] { "z", "a", "b", "c", "d", "e" }, order);
    }

    [Fact]
    public void SubtreeWithRoot_IncludesRootAndDescendants()
    {
        var parents = new Dictionary<string, string?>
        { ["a"] = null, ["b"] = "a", ["c"] = "b", ["t"] = null };
        Assert.Equal(new HashSet<string> { "a", "b", "c" }, TaskTree.SubtreeWithRoot(parents, "a"));
    }
}
