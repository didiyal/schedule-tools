namespace TodoTimer.Models;

public enum DropKind { SiblingBefore, SiblingAfter, Child }

public sealed record DropAction(DropKind Kind, string TargetId);

/// <summary>
/// 拖拽落点解析 —— 纯函数（v1.2 的三区语义，落点逻辑从事件层剥干净以便直测；
/// v1.2 拖拽「成为子任务」失效 bug 的修复点：解析集中一处，行为可枚举）。
/// 目标卡片上/下 25% = 同父兄弟插入（顶层维持同日期规则）；中间 50% = 成为其
/// 子任务（can_adopt 校验成环与 3 层上限）。
/// </summary>
public static class DropResolver
{
    /// <summary>横轴分模式（Notion 式）：贴左=同级(上/下半分前/后)，偏右=成为子级。
    /// 两轴独立天然不抖；yRatio 仅在同级模式内取上/下。</summary>
    public static DropAction? Resolve(IReadOnlyDictionary<string, string?> parents,
        IReadOnlyDictionary<string, TodoItem> byId,
        string dragId, string targetId, bool childMode, double yRatio)
    {
        if (dragId == targetId) return null;
        if (!byId.TryGetValue(dragId, out var dw)) return null;
        if (!byId.TryGetValue(targetId, out var tw)) return null;

        if (childMode)
            return TaskTree.CanAdopt(parents, targetId, dragId)
                ? new DropAction(DropKind.Child, targetId)
                : null;

        // 兄弟插入：仅限同父；顶层维持同日期规则
        if (ParentOf(parents, dragId) != ParentOf(parents, targetId)) return null;
        if (ParentOf(parents, dragId) is null
            && !string.Equals(dw.Date, tw.Date, StringComparison.Ordinal))
            return null;
        if (TaskTree.Descendants(parents, dragId).Contains(targetId)) return null;
        return new DropAction(
            yRatio < 0.5 ? DropKind.SiblingBefore : DropKind.SiblingAfter, targetId);
    }

    private static string? ParentOf(IReadOnlyDictionary<string, string?> parents, string id)
        => parents.TryGetValue(id, out var p) ? p : null;
}
