using System.Globalization;
using System.Windows;

namespace TodoTimer.Models;

public enum FilterState { All, Active, Completed }

/// <summary>渲染计划行：日组头或任务卡（对齐 Python v1.2 条目元组语义）。</summary>
public sealed class PlanRow
{
    public bool IsHeader { get; init; }
    public string Label { get; init; } = "";
    public TodoItem Todo { get; init; } = new();
    public int Depth { get; init; }
    public bool Expandable { get; init; }
    public bool Expanded { get; init; } = true;
    public bool Dim { get; init; }
    public bool ShowDate { get; init; }
    public string Progress { get; init; } = "";
    public string DateBadge { get; init; } = "";
    public string ExpandGlyph { get; init; } = "";
    public Thickness CardMargin { get; init; }
}

/// <summary>
/// 纯计算渲染计划 —— 对齐 Python v1.2 _compute_plan（分页按拍板去掉）：
/// 每日一棵树：日期倒序日组，组内是当天根节点的森林（树包含优先于日期分组，
/// 跨日子任务跟父走并显示日期角标）；排序与完成态解耦（重点 → order）；
/// 筛选/搜索是节点谓词，未命中者剔除、命中者的祖先链保留并弱显；
/// 折叠发出节点本身、跳过整棵子树；父卡片子进度只数直接子级。
/// </summary>
public static class RenderPlan
{
    private static readonly string[] Weekdays = { "周一", "周二", "周三", "周四", "周五", "周六", "周日" };
    private const int IndentPx = 18;

    public static string FormatDateCn(string dateStr)
    {
        if (DateOnly.TryParseExact(dateStr, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var d))
        {
            var wd = d.DayOfWeek == DayOfWeek.Sunday ? Weekdays[6] : Weekdays[(int)d.DayOfWeek - 1];
            return $"{d.Year}年{d.Month}月{d.Day}日 {wd}";
        }
        return dateStr;
    }

    public static string FormatDateShort(string dateStr)
    {
        if (DateOnly.TryParseExact(dateStr, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var d))
            return $"{d.Month}月{d.Day}日";
        return dateStr;
    }

    public static List<PlanRow> Compute(IReadOnlyList<TodoItem> todos, FilterState filter,
        string? search, IReadOnlySet<string> collapsed, string? today = null)
    {
        today ??= TodoItem.TodayStr();

        var byId = new Dictionary<string, TodoItem>();
        foreach (var t in todos) byId[t.Id] = t;          // 重复 id 后者覆盖，同 Python dict

        var parents = TaskTree.Normalize(
            byId.ToDictionary(kv => kv.Key, kv => kv.Value.Parent));
        var chMap = TaskTree.ChildrenMap(parents);

        // 兄弟序：重点在前 → order（与完成态解耦，完成不移动）
        var childrenSorted = new Dictionary<string, List<string>>();
        foreach (var kv in chMap)
            childrenSorted[kv.Key] = kv.Value
                .OrderByDescending(t => byId[t].Starred)
                .ThenBy(t => byId[t].Order)
                .ToList();

        var st = (search ?? "").ToLowerInvariant();
        bool Matches(TodoItem w) => filter switch
        {
            FilterState.Active => !w.Completed,
            FilterState.Completed => w.Completed,
            _ => true,
        } && (st.Length == 0 || w.Text.ToLowerInvariant().Contains(st));

        // 谓词命中集 + 祖先链保留（retained 中未命中者弱显撑结构）
        var keep = new HashSet<string>();
        foreach (var (tid, w) in byId)
            if (Matches(w)) keep.Add(tid);
        var retained = new HashSet<string>(keep);
        foreach (var tid in keep)
        {
            var seen = new HashSet<string>();
            var cur = parents.TryGetValue(tid, out var p0) ? p0 : null;
            while (cur is not null && parents.ContainsKey(cur) && seen.Add(cur))
            {
                retained.Add(cur);
                cur = parents.TryGetValue(cur, out var p) ? p : null;
            }
        }

        // 根节点按自身日期分组（空日期 → 今日）
        var rootsByDate = new Dictionary<string, List<string>>();
        foreach (var (tid, pid) in parents)
        {
            if (pid is not null) continue;
            var d = byId[tid].Date;
            if (string.IsNullOrEmpty(d)) d = today;
            if (!rootsByDate.TryGetValue(d, out var list)) rootsByDate[d] = list = new();
            list.Add(tid);
        }

        var entries = new List<PlanRow>();

        foreach (var dk in rootsByDate.Keys.OrderByDescending(k => k, StringComparer.Ordinal))
        {
            var dayCards = new List<PlanRow>();

            void Emit(string tid, int depth, string parentDate)
            {
                var w = byId[tid];
                var kidsAll = chMap.TryGetValue(tid, out var ks) ? ks : new List<string>();
                var expandable = kidsAll.Count > 0;
                var prog = "";
                if (expandable)
                {
                    var done = kidsAll.Count(k => byId[k].Completed);
                    prog = $"{done}/{kidsAll.Count}";
                }
                if (retained.Contains(tid))
                {
                    var showDate = depth > 0 && w.Date != parentDate;
                    dayCards.Add(new PlanRow
                    {
                        Todo = w,
                        Depth = depth,
                        Expandable = expandable,
                        Expanded = !collapsed.Contains(tid),
                        Dim = !keep.Contains(tid),
                        ShowDate = showDate,
                        Progress = prog,
                        DateBadge = showDate ? FormatDateShort(w.Date) : "",
                        ExpandGlyph = !expandable ? "" : collapsed.Contains(tid) ? "▸" : "▾",
                        CardMargin = new Thickness(depth * IndentPx, 2, 0, 2),
                    });
                }
                if (collapsed.Contains(tid)) return;   // 折叠：发出节点本身，跳过整棵子树
                if (childrenSorted.TryGetValue(tid, out var kids))
                    foreach (var k in kids) Emit(k, depth + 1, w.Date);
            }

            if (childrenSorted.TryGetValue("", out var roots))    // "" = 顶层桶
                foreach (var tid in roots)
                    if (rootsByDate[dk].Contains(tid))
                        Emit(tid, 0, "");

            if (dayCards.Count > 0)
            {
                var label = dk == today ? $"▎今日 · {dayCards.Count}项"
                                        : $"▎{FormatDateCn(dk)} · {dayCards.Count}项";
                entries.Add(new PlanRow { IsHeader = true, Label = label });
                entries.AddRange(dayCards);
            }
        }

        if (!entries.Any(e => !e.IsHeader))
        {
            entries.Clear();
            entries.Add(new PlanRow { IsHeader = true, Label = "暂无待办" });
        }
        return entries;
    }
}
