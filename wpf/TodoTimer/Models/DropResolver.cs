namespace TodoTimer.Models;

public enum DropKind { SiblingBefore, SiblingAfter, Child, Promote }

/// <summary>横轴三段意图：最左提升带=拖成主任务，内容左缘=同级，偏右=成为子级。</summary>
public enum DropIntent { Promote, Sibling, Child }

public sealed record DropAction(DropKind Kind, string TargetId, bool Before = false);

/// <summary>
/// 拖拽落点解析 —— 纯函数（v1.2 的三区语义，落点逻辑从事件层剥干净以便直测；
/// v1.2 拖拽「成为子任务」失效 bug 的修复点：解析集中一处，行为可枚举）。
/// 目标卡片上/下 25% = 同父兄弟插入（顶层维持同日期规则）；中间 50% = 成为其
/// 子任务（can_adopt 校验成环与 3 层上限）。
/// </summary>
public static class DropResolver
{
    /// <summary>横轴三段分意图（Notion 式改进）：最左提升带=拖成主任务(日期跟随目标组)、
    /// 内容左缘=同级插入(跨日期允许, 顺带改期到目标组)、偏右=成为子级。
    /// 两轴独立天然不抖；yRatio 决定前/后插。</summary>
    public static DropAction? Resolve(IReadOnlyDictionary<string, string?> parents,
        IReadOnlyDictionary<string, TodoItem> byId,
        string dragId, string targetId, DropIntent intent, double yRatio)
    {
        if (dragId == targetId) return null;
        if (!byId.TryGetValue(dragId, out _)) return null;
        if (!byId.TryGetValue(targetId, out _)) return null;

        if (intent == DropIntent.Child)
            return TaskTree.CanAdopt(parents, targetId, dragId)
                ? new DropAction(DropKind.Child, targetId)
                : null;

        // 同级/提升都不能插进自己的子树
        if (TaskTree.Descendants(parents, dragId).Contains(targetId)) return null;
        var before = yRatio < 0.5;

        if (intent == DropIntent.Promote)
            return new DropAction(DropKind.Promote, targetId, before);

        // 兄弟插入：仅限同父；跨日期已放开（落位时改期到目标组）
        if (ParentOf(parents, dragId) != ParentOf(parents, targetId)) return null;
        return new DropAction(
            before ? DropKind.SiblingBefore : DropKind.SiblingAfter, targetId);
    }

    private static string? ParentOf(IReadOnlyDictionary<string, string?> parents, string id)
        => parents.TryGetValue(id, out var p) ? p : null;
}
