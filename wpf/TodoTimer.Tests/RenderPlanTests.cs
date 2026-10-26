using TodoTimer.Models;

namespace TodoTimer.Tests;

/// <summary>渲染计划语义测试 —— 断言对照 Python v1.2 _compute_plan。</summary>
public sealed class RenderPlanTests
{
    private const string Today = "2026-10-23";

    private static TodoItem T(string id, string text, string date, bool completed = false,
        bool starred = false, int order = 0, string? parent = null) => new()
    {
        Id = id, Text = text, Date = date, Completed = completed,
        Starred = starred, Order = order, Parent = parent,
    };

    private static List<PlanRow> Compute(List<TodoItem> todos, FilterState filter = FilterState.All,
        string? search = null, HashSet<string>? collapsed = null, bool todayOnly = false)
        => RenderPlan.Compute(todos, filter, search, collapsed ?? new HashSet<string>(), Today, todayOnly);

    private static List<string> CardIds(List<PlanRow> rows)
        => rows.Where(r => !r.IsHeader).Select(r => r.Todo.Id).ToList();

    [Fact]
    public void SiblingOrder_StarredFirstThenOrder_CompletionDoesNotMove()
    {
        var todos = new List<TodoItem>
        {
            T("a", "普通后", Today, order: 3),
            T("b", "次序一", Today, order: 1),
            T("s", "重点", Today, order: 9, starred: true),
            T("c", "已完成", Today, order: 0, completed: true),   // 完成不移动，仍在重点之后按 order
        };
        var rows = Compute(todos);
        Assert.Equal(new[] { "s", "c", "b", "a" }, CardIds(rows));
        Assert.True(rows[1].Todo.Starred);
    }

    [Fact]
    public void DayGroups_Descending_WithHeaders()
    {
        var todos = new List<TodoItem>
        {
            T("old", "旧任务", "2026-10-01"),
            T("t", "今日任务", Today),
        };
        var rows = Compute(todos);
        Assert.Equal(new[] { "t", "old" }, CardIds(rows));
        Assert.StartsWith("▎今日 · 1项", rows[0].Label);
        Assert.StartsWith("▎2026年10月1日 ", rows[2].Label);
    }

    [Fact]
    public void CrossDateChild_FollowsParent_WithDateBadge()
    {
        var todos = new List<TodoItem>
        {
            T("p", "父", Today),
            T("c", "跨日子任务", "2026-10-22", parent: "p"),
        };
        var rows = Compute(todos);
        // 只有一个日组（父日期），子跟父走并带角标
        Assert.Single(rows.Where(r => r.IsHeader));
        Assert.Equal(new[] { "p", "c" }, CardIds(rows));
        var cRow = rows[2];
        Assert.True(cRow.ShowDate);
        Assert.Equal("10月22日", cRow.DateBadge);
        Assert.Equal(1, cRow.Depth);
        Assert.StartsWith("▎今日 · 2项", rows[0].Label);   // 组头计数含子卡
    }

    [Fact]
    public void FilterActive_DropsCompleted()
    {
        var todos = new List<TodoItem>
        {
            T("p", "父", Today),
            T("c", "已完成子", Today, completed: true, parent: "p"),
            T("s", "未完成子", Today, parent: "p"),
        };
        var rows = Compute(todos, FilterState.Active);
        Assert.Equal(new[] { "p", "s" }, CardIds(rows));
        Assert.All(rows.Where(r => !r.IsHeader), r => Assert.False(r.Todo.Completed));
    }

    [Fact]
    public void FilterCompleted_KeepsAncestorChainDimmed()
    {
        var todos = new List<TodoItem>
        {
            T("p", "父", Today),
            T("c", "已完成子", Today, completed: true, parent: "p"),
            T("s", "未完成子", Today, parent: "p"),
        };
        var rows = Compute(todos, FilterState.Completed);
        Assert.Equal(new[] { "p", "c" }, CardIds(rows));
        Assert.True(rows[1].Dim);        // 祖先链弱显撑结构
        Assert.False(rows[2].Dim);
        Assert.True(rows[1].Expanded);   // 祖先必须展开才能露出命中的子
    }

    [Fact]
    public void Search_IsSubstringPredicate_AndedWithFilter()
    {
        var todos = new List<TodoItem>
        {
            T("1", "买牛奶", Today),
            T("2", "写周报", Today),
            T("3", "牛奶完成版", Today, completed: true),
        };
        Assert.Equal(new[] { "1", "3" }, CardIds(Compute(todos, search: "牛")));
        // 搜索与状态筛选取交集：牛 + 未完成 → 只剩 1
        Assert.Equal(new[] { "1" }, CardIds(Compute(todos, FilterState.Active, "牛")));
        // 全被过滤 → 空提示
        var none = Compute(todos, FilterState.Active, "不存在的词");
        Assert.Single(none);
        Assert.Equal("暂无待办", none[0].Label);
    }

    [Fact]
    public void Collapse_HidesSubtree_KeepsNodeAndProgress()
    {
        var todos = new List<TodoItem>
        {
            T("p", "父", Today),
            T("c", "子", Today, completed: true, parent: "p"),
        };
        var rows = Compute(todos, collapsed: new HashSet<string> { "p" });
        Assert.Equal(new[] { "p" }, CardIds(rows));
        Assert.False(rows[1].Expanded);
        Assert.Equal("▸", rows[1].ExpandGlyph);
        Assert.Equal("1/1", rows[1].Progress);
    }

    [Fact]
    public void Progress_CountsDirectChildrenOnly()
    {
        var todos = new List<TodoItem>
        {
            T("p", "父", Today),
            T("c1", "子一", Today, completed: true, parent: "p"),
            T("c2", "子二", Today, parent: "p"),
            T("g3", "孙三", Today, parent: "c2"),
        };
        var rows = Compute(todos);
        Assert.Equal("1/2", rows[1].Progress);
    }

    [Fact]
    public void TodayOnly_DropsCrossDateNodes_KeepsTodayChain()
    {
        var todos = new List<TodoItem>
        {
            T("p", "今日父", Today),
            T("c", "昨日子", "2026-10-22", parent: "p"),
            T("t", "今日子", Today, parent: "p"),
        };
        var rows = Compute(todos, todayOnly: true);
        var ids = rows.Where(r => !r.IsHeader && !r.IsTodaySlot).Select(r => r.Todo.Id).ToList();
        Assert.Equal(new[] { "p", "t" }, ids);        // 昨日子被剔除, 今日链保留
        // 只有旧日期任务时: 全部剔除, 但今日槽仍在
        var old = Compute(new List<TodoItem> { T("o", "旧任务", "2026-10-01") }, todayOnly: true);
        Assert.Empty(old.Where(r => !r.IsHeader && !r.IsTodaySlot));
    }

    [Fact]
    public void EmptyList_ShowsFallback()
    {
        var rows = Compute(new List<TodoItem>());
        Assert.Single(rows);
        Assert.Equal("暂无待办", rows[0].Label);
    }

    [Fact]
    public void FormatDateCn_WithWeekday()
    {
        Assert.Equal("2026年10月23日 周五", RenderPlan.FormatDateCn("2026-10-23"));
        Assert.Equal("2026年10月1日 周四", RenderPlan.FormatDateCn("2026-10-01"));
    }
}
