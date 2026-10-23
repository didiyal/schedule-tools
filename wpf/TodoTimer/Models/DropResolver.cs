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
    public static DropAction? Resolve(IReadOnlyDictionary<string, string?> parents,
        IReadOnlyDictionary<string, TodoItem> byId,
        string dragId, string targetId, double yRatio)
    {
        if (dragId == targetId) return null;
        if (!byId.TryGetValue(dragId, out var dw)) return null;
        if (!byId.TryGetValue(targetId, out var tw)) return null;

        if (yRatio < 0.25 || yRatio > 0.75)
        {
            // 兄弟插入：仅限同父；顶层维持同日期规则
            if (ParentOf(parents, dragId) != ParentOf(parents, targetId)) return null;
            if (ParentOf(parents, dragId) is null
                && !string.Equals(dw.Date, tw.Date, StringComparison.Ordinal))
                return null;
            if (TaskTree.Descendants(parents, dragId).Contains(targetId)) return null;
            return new DropAction(
                yRatio < 0.25 ? DropKind.SiblingBefore : DropKind.SiblingAfter, targetId);
        }
        return TaskTree.CanAdopt(parents, targetId, dragId)
            ? new DropAction(DropKind.Child, targetId)
            : null;
    }

    private static string? ParentOf(IReadOnlyDictionary<string, string?> parents, string id)
        => parents.TryGetValue(id, out var p) ? p : null;
}
