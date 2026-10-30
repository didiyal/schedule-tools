namespace TodoTimer.Models;

/// <summary>
/// 每日顺延（v1.5，用户拍板 2026-10-27）：
/// 昨天没完成的活儿搬到今天，昨天有进度的树留"已结转"快照。
/// 规则：
///   · 有进度才留档——旧日树存在已完成后代 → 旧根标记 rolled_at 原样保留（未完成后代摘走）；
///   · 无任何进度的树 → 整棵改期到今天，不产生残根；
///   · 顺延子树挂到今天的新根下（新根同名、date=today）；
///   · 已完成后代底下的未完成孙任务也顺延，摘挂到新根（深度回落仍 ≤3 层）；
///   · 同名不合并——今天已有同名根也另建新根（宁可重复勿误伤，手动拖拽合并已支持）；
///   · 幂等：跑第二遍时不存在 date&lt;today 的未完成节点，无副作用。
/// 变更都是普通条目修改/新建（走既有 LWW 同步），服务端与手机端零改动。
/// </summary>
public static class DayRollover
{
    /// <summary>对整表执行顺延。返回 (是否产生变更, 变更说明)。</summary>
    public static (bool Changed, string Summary) Run(List<TodoItem> todos, string today)
    {
        // 多端同步历史可能产生重复 id（LWW 各端造号撞号）——取首个，重复项跳过，
        // 否则 ToDictionary 直接崩掉启动（2026-10-30 用户报开发版启动闪退 Key:213）
        var byId = new Dictionary<string, TodoItem>();
        foreach (var t in todos)
            if (!byId.ContainsKey(t.Id)) byId[t.Id] = t;
        var parents = TaskTree.Normalize(byId.ToDictionary(kv => kv.Key, kv => kv.Value.Parent));
        foreach (var t in todos) t.Parent = parents[t.Id];
        var chMap = TaskTree.ChildrenMap(parents);

        var staleRoots = todos
            .Where(t => t.Parent is null && !t.Completed
                        && t.RolledAt is null                  // 已结转=历史快照，永不再处理（幂等）
                        && string.CompareOrdinal(t.Date, today) < 0)
            .Select(t => t.Id)
            .ToList();
        if (staleRoots.Count == 0) return (false, "");

        var nextId = todos.Select(t => int.TryParse(t.Id, out var n) ? n : 0).DefaultIfEmpty(0).Max() + 1;
        var nextOrder = todos.Select(t => t.Order).DefaultIfEmpty(0).Max() + 1;
        var changes = 0;
        var summaries = new List<string>();

        foreach (var rootId in staleRoots)
        {
            var root = byId[rootId];
            var subtree = TaskTree.SubtreeWithRoot(parents, rootId).Select(id => byId[id]).ToList();
            var hasProgress = subtree.Any(t => t.Completed);

            // 收集全部未完成节点（含已完成子树下的孙任务），记录各自旧父；
            // 根自身先入列（无进度时整棵走需要它），有进度分支再剔除（根留档不搬）。
            // 未完成叶不管 date——只要不在今天视角（挂在旧日根下），都该被今天的新根收编
            var carry = new List<(TodoItem Item, string? OldParent)>();
            carry.Add((root, root.Parent));
            void Walk(string id, int depth)
            {
                var t = byId[id];
                if (depth > 0 && !t.Completed)
                    carry.Add((t, t.Parent));
                if (chMap.TryGetValue(id, out var kids))
                    foreach (var k in kids) Walk(k, depth + 1);
            }
            Walk(rootId, 0);

            if (!hasProgress)
            {
                foreach (var (item, _) in carry)
                {
                    item.Date = today;
                    item.RolledAt = null;
                }
                changes += carry.Count;
                summaries.Add(carry.Count == 1 && carry[0].Item.Id == rootId
                    ? $"「{root.Text}」顺延" : $"「{root.Text}」整棵顺延");
                continue;
            }

            // ── 有进度：旧根留档结转，未完成节点全部收编到今天的新根 ──
            carry.RemoveAll(c => c.Item.Id == rootId);       // 根不搬
            // 有进度的旧根一律结转留档；无未完成叶则只留档、不建新根
            root.RolledAt = root.RolledAt ?? today;
            changes++;
            if (carry.Count == 0) continue;

            var newRoot = new TodoItem                       // 今天的新根（同名承接）
            {
                Id = (nextId++).ToString(),
                Text = root.Text,
                Date = today,
                Order = nextOrder++,
                Starred = root.Starred,                      // 重点标识跟任务走（2026-10-28 用户报）
                RolledFrom = root.Id,                        // 结转链：完成新根时同步翻篇旧根
            };
            todos.Add(newRoot);
            byId[newRoot.Id] = newRoot;
            parents[newRoot.Id] = null;
            changes++;

            foreach (var (item, oldParent) in carry)
            {
                item.Date = today;
                // 直接旧根子 → 挂新根；父不在搬运清单（=已完成节点）→ 摘出挂新根（深度回落）；
                // 其余（父也在搬运中，父已指向新根）→ 维持相对结构
                if (oldParent == rootId || !carry.Any(c => c.Item.Id == oldParent))
                    item.Parent = newRoot.Id;
                changes++;
            }
            summaries.Add($"「{root.Text}」结转留档 → 今日「{newRoot.Text}」({carry.Count} 项)");
        }

        return (changes > 0, string.Join("；", summaries));
    }
}
