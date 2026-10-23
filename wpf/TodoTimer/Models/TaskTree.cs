namespace TodoTimer.Models;

using System.Linq;

/// <summary>
/// 任务树纯函数 —— 与 Python 版 workspace/src/tree.py 逐条对齐。
/// 只操作 {id: parent} 映射（parent=null 表示顶层），不依赖任何 UI 类型。
/// 层级约定：depth 0=主任务，1=子任务，2=子子任务。
/// 所有遍历带 seen 防御：外部手改 JSON 产生的环不会死循环。
/// </summary>
public static class TaskTree
{
    public const int MaxDepthIndex = 2;   // 最深层级号（0 起）→ 主/子/子子 共 3 层

    /// <summary>{id: parent} → {parent: [cid,...]}（无序，调用方自行排序）。
    /// parent=null 归入 "" 哨兵键（Dictionary 的 null 键查找会抛异常，不用 null）。</summary>
    public static Dictionary<string, List<string>> ChildrenMap(IReadOnlyDictionary<string, string?> parents)
    {
        var m = new Dictionary<string, List<string>>();
        foreach (var (tid, pid) in parents)
        {
            var key = pid ?? "";
            if (!m.TryGetValue(key, out var list)) m[key] = list = new List<string>();
            list.Add(tid);
        }
        return m;
    }

    /// <summary>节点深度（顶层=0）。环/悬空引用遇即停，返回已走层数。</summary>
    public static int DepthOf(IReadOnlyDictionary<string, string?> parents, string tid)
    {
        int d = 0;
        var seen = new HashSet<string>();
        var cur = tid;
        while (parents.TryGetValue(cur, out var parent) && parent is not null)
        {
            cur = parent;
            if (seen.Contains(cur) || !parents.ContainsKey(cur)) break;
            seen.Add(cur);
            d++;
        }
        return d;
    }

    /// <summary>root 的全部后代（不含 root）。防御环。</summary>
    public static HashSet<string> Descendants(IReadOnlyDictionary<string, string?> parents, string root)
    {
        var ch = ChildrenMap(parents);
        var set = new HashSet<string>();
        var stack = new Stack<string>();
        if (ch.TryGetValue(root, out var kids))
            foreach (var k in kids) stack.Push(k);
        while (stack.Count > 0)
        {
            var t = stack.Pop();
            if (!set.Add(t)) continue;
            if (ch.TryGetValue(t, out var ks))
                foreach (var k in ks) stack.Push(k);
        }
        return set;
    }

    /// <summary>root 及其全部后代（删除整枝用）</summary>
    public static HashSet<string> SubtreeWithRoot(IReadOnlyDictionary<string, string?> parents, string root)
    {
        var set = Descendants(parents, root);
        set.Add(root);
        return set;
    }

    /// <summary>子树层数（单独节点=1）。防御环。</summary>
    public static int SubtreeHeight(IReadOnlyDictionary<string, string?> parents, string root)
    {
        var ch = ChildrenMap(parents);
        int best = 0;
        var seen = new HashSet<string>();
        var stack = new Stack<(string T, int H)>();
        stack.Push((root, 1));
        while (stack.Count > 0)
        {
            var (t, h) = stack.Pop();
            if (!seen.Add(t)) continue;
            if (h > best) best = h;
            if (ch.TryGetValue(t, out var ks))
                foreach (var k in ks) stack.Push((k, h + 1));
        }
        return best;
    }

    /// <summary>把 dragged（含其子树）挂到 newParent 下是否合法：
    /// 父存在、不是自己或自己的后代、深度不超限。</summary>
    public static bool CanAdopt(IReadOnlyDictionary<string, string?> parents, string? newParent,
        string dragged, int maxDepthIndex = MaxDepthIndex)
    {
        if (newParent == dragged) return false;
        if (newParent is not null && !parents.ContainsKey(newParent)) return false;
        if (newParent is not null && Descendants(parents, dragged).Contains(newParent)) return false;
        int newDepth = newParent is null ? 0 : DepthOf(parents, newParent) + 1;
        return newDepth + SubtreeHeight(parents, dragged) - 1 <= maxDepthIndex;
    }

    /// <summary>装载自愈：悬空引用/环成员/超深节点的 parent 置 null（拍平为顶层）。
    /// 返回修正后的新 dict，不改入参。</summary>
    public static Dictionary<string, string?> Normalize(IReadOnlyDictionary<string, string?> parents,
        int maxDepthIndex = MaxDepthIndex)
    {
        var fixedMap = new Dictionary<string, string?>();
        foreach (var (t, p) in parents)
            fixedMap[t] = (p is null || parents.ContainsKey(p)) ? p : null;

        // 环成员拍平：向上游走能回到自身（或步数超界）的节点
        foreach (var t in fixedMap.Keys.ToList())
        {
            var seen = new HashSet<string>();
            var cur = t;
            int steps = 0;
            while (fixedMap.TryGetValue(cur, out var p) && p is not null)
            {
                cur = p;
                steps++;
                if (cur == t || seen.Contains(cur) || steps > fixedMap.Count)
                {
                    fixedMap[t] = null;
                    break;
                }
                seen.Add(cur);
            }
        }

        // 超深拍平：每轮重算深度，只拍平「父不是违规者」的最浅违规层，
        // 其后代深度下一轮自然回落 —— 避免把可保留的子树过度拍平
        for (int round = 0; round < fixedMap.Count + 2; round++)
        {
            var ch = ChildrenMap(fixedMap);
            var depth = new Dictionary<string, int>();
            var queue = new Queue<(string T, int D)>();
            foreach (var (t, p) in fixedMap)
                if (p is null) queue.Enqueue((t, 0));
            while (queue.Count > 0)
            {
                var (t, d) = queue.Dequeue();
                if (depth.ContainsKey(t)) continue;   // 环残留防御
                depth[t] = d;
                if (ch.TryGetValue(t, out var ks))
                    foreach (var k in ks) queue.Enqueue((k, d + 1));
            }

            var violators = depth.Where(kv => kv.Value > maxDepthIndex).Select(kv => kv.Key).ToHashSet();
            if (violators.Count == 0) break;
            foreach (var t in violators)
                if (fixedMap[t] is not null && !violators.Contains(fixedMap[t]!))
                    fixedMap[t] = null;
        }
        return fixedMap;
    }

    /// <summary>按给定兄弟序深度优先展开，父必在子前。
    /// childrenSorted: {parent: [cid 按显示序]}；roots: 顶层 id 按显示序。</summary>
    public static List<string> DfsOrder(IReadOnlyDictionary<string, List<string>> childrenSorted,
        IEnumerable<string> roots)
    {
        var result = new List<string>();
        var stack = new Stack<string>();
        var rootList = roots.ToList();
        for (int i = rootList.Count - 1; i >= 0; i--) stack.Push(rootList[i]);
        while (stack.Count > 0)
        {
            var t = stack.Pop();
            result.Add(t);
            if (childrenSorted.TryGetValue(t, out var kids))
                for (int i = kids.Count - 1; i >= 0; i--) stack.Push(kids[i]);
        }
        return result;
    }
}
