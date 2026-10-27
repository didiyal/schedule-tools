using TodoTimer.Models;
using Xunit;

namespace TodoTimer.Tests;

/// <summary>每日顺延（DayRollover）单测——用户场景：昨天"标书/A/B(B完成)"→今天新根+A顺延、昨天留档。</summary>
public class DayRolloverTests
{
    private static readonly string Y = "2026-10-26";
    private static readonly string T = "2026-10-27";

    private static TodoItem New(string id, string text, string? parent, string date, bool completed = false)
        => new() { Id = id, Text = text, Parent = parent, Date = date, Completed = completed };

    [Fact]
    public void UserScenario_PartialDone_SplitsTree()
    {
        // 昨天：标书(根) ─ A(未完成) ─ B(已完成)
        var todos = new List<TodoItem>
        {
            New("1", "标书", null, Y),
            New("2", "A", "1", Y),
            New("3", "B", "1", Y, completed: true),
        };
        var (changed, summary) = DayRollover.Run(todos, T);

        Assert.True(changed);
        Assert.Contains("标书", summary);
        var root = todos.Single(t => t.Id == "1");
        Assert.Equal(Y, root.Date);                      // 旧根留档
        Assert.Equal(T, root.RolledAt);                  // 已结转标记
        Assert.True(todos.Single(t => t.Id == "3").Completed);   // B 原样

        var newRoot = todos.Single(t => t.Text == "标书" && t.Date == T);
        Assert.Null(newRoot.Parent);
        var a = todos.Single(t => t.Id == "2");
        Assert.Equal(newRoot.Id, a.Parent);               // A 挂到新根
        Assert.Equal(T, a.Date);                          // A 改期
    }

    [Fact]
    public void NoProgress_WholeTreeMoves_NoStump()
    {
        var todos = new List<TodoItem>
        {
            New("1", "写周报", null, Y),
            New("2", "查数据", "1", Y),
        };
        var (changed, _) = DayRollover.Run(todos, T);
        Assert.True(changed);
        Assert.All(todos, t => Assert.Equal(T, t.Date));       // 全到今天
        Assert.All(todos, t => Assert.Null(t.RolledAt));       // 不留结转
        Assert.Equal(2, todos.Count);                            // 无新根
        Assert.Equal("1", todos.Single(t => t.Id == "2").Parent);
    }

    [Fact]
    public void AllDone_RootMarkedRolled_NoCarry()
    {
        // 全完成的旧日树：留档+结转标记（历史快照语义），但不产生新根/搬运
        var todos = new List<TodoItem>
        {
            New("1", "标书", null, Y),
            New("2", "A", "1", Y, completed: true),
            New("3", "B", "1", Y, completed: true),
        };
        var (changed, _) = DayRollover.Run(todos, T);
        Assert.True(changed);
        var root = todos.Single(t => t.Id == "1");
        Assert.Equal(T, root.RolledAt);   // 有进度即结转
        Assert.Equal(3, todos.Count);      // 无新根无搬运
    }

    [Fact]
    public void SimpleTask_Alone_MovesDirectly()
    {
        var todos = new List<TodoItem> { New("7", "独立任务", null, Y) };
        var (changed, _) = DayRollover.Run(todos, T);
        Assert.True(changed);
        var t7 = todos.Single();
        Assert.Equal(T, t7.Date);
        Assert.Null(t7.RolledAt);
    }

    [Fact]
    public void Idempotent_SecondRunNoop()
    {
        var todos = new List<TodoItem>
        {
            New("1", "标书", null, Y),
            New("2", "A", "1", Y),
            New("3", "B", "1", Y, completed: true),
        };
        DayRollover.Run(todos, T);
        var snapshot = todos.Select(t => (t.Id, t.Text, t.Parent, t.Date, t.Completed, t.RolledAt)).ToList();
        var (changed, _) = DayRollover.Run(todos, T);
        Assert.False(changed);
        Assert.Equal(snapshot.Count, todos.Count);
    }

    [Fact]
    public void GrandchildUnderCompleted_Parent_CarriedToNewRoot()
    {
        // C 完成，C1 未完成：C1 也要顺延，直接挂新根
        var todos = new List<TodoItem>
        {
            New("1", "标书", null, Y),
            New("2", "C", "1", Y, completed: true),
            New("3", "C1", "2", Y),
        };
        var (changed, _) = DayRollover.Run(todos, T);
        Assert.True(changed);
        var newRoot = todos.Single(t => t.Text == "标书" && t.Date == T);
        var c1 = todos.Single(t => t.Id == "3");
        Assert.Equal(newRoot.Id, c1.Parent);
        Assert.Equal(T, c1.Date);
        var c = todos.Single(t => t.Id == "2");
        Assert.True(c.Completed);                 // C 留在昨天
        Assert.Equal(Y, c.Date);
        Assert.Equal("1", c.Parent);
    }

    [Fact]
    public void TodayTask_NotTouched()
    {
        var todos = new List<TodoItem> { New("1", "今天的事", null, T) };
        var (changed, _) = DayRollover.Run(todos, T);
        Assert.False(changed);
    }

    [Fact]
    public void DeepSubtree_StaysTogether()
    {
        // 根─A─A1（都未完成）：整枝顺延时相对结构保持
        var todos = new List<TodoItem>
        {
            New("1", "工程", null, Y),
            New("2", "A", "1", Y),
            New("3", "A1", "2", Y),
            New("4", "B", "1", Y, completed: true),
        };
        var (changed, _) = DayRollover.Run(todos, T);
        Assert.True(changed);
        var newRoot = todos.Single(t => t.Text == "工程" && t.Date == T);
        var a = todos.Single(t => t.Id == "2");
        var a1 = todos.Single(t => t.Id == "3");
        Assert.Equal(newRoot.Id, a.Parent);
        Assert.Equal("2", a1.Parent);      // 深层结构原样跟着 A
        Assert.Equal(T, a1.Date);
    }

    [Fact]
    public void CrossDateLeaf_AlsoCollected_UnderNewRoot()
    {
        // 168 场景：旧根的未完成叶早已跨日（date=今天）——仍要被今日新根收编，
        // 否则今天看不到这棵树（用户报"今日里没看到结转的任务"）
        var todos = new List<TodoItem>
        {
            New("1", "工作流", null, Y),
            New("2", "设计", "1", Y, completed: true),
            New("3", "测试流水线", "1", T),          // 未完成但已是今天日期
        };
        var (changed, summary) = DayRollover.Run(todos, T);
        Assert.True(changed);
        var newRoot = todos.Single(t => t.Text == "工作流" && t.Date == T);
        Assert.Equal("1", newRoot.RolledFrom);              // 结转链
        var leaf = todos.Single(t => t.Id == "3");
        Assert.Equal(newRoot.Id, leaf.Parent);               // 被新根收编
        Assert.Equal(T, leaf.Date);
        var old = todos.Single(t => t.Id == "1");
        Assert.Equal(T, old.RolledAt);
    }

    [Fact]
    public void AllLeavesCompleted_NoNewRoot_OnlyArchiveMark()
    {
        // 全部子叶已完成：旧根结转留档即可，不建空新根
        var todos = new List<TodoItem>
        {
            New("1", "工作流", null, Y),
            New("2", "设计", "1", Y, completed: true),
            New("3", "测试", "1", Y, completed: true),
        };
        var (changed, _) = DayRollover.Run(todos, T);
        Assert.True(changed);
        Assert.Equal(3, todos.Count);                        // 无新根
        Assert.Equal(T, todos.Single(t => t.Id == "1").RolledAt);
    }
}
