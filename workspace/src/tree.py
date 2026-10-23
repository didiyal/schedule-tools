"""任务树纯函数 —— GUI(MainApp) 与 headless(cli_headless) 共用。

只操作 {todo_id: parent_id} 映射（parent_id=None 表示顶层），
不 import TodoWidget、不碰 tkinter —— headless 路径必须保持无 GUI 依赖。

层级约定：depth 0=主任务，1=子任务，2=子子任务；MAX_DEPTH_INDEX 为最深层级号。
所有遍历带 seen/steps 防御：外部手改 JSON 产生的环不会死循环。
"""

MAX_DEPTH_INDEX = 2   # 最深层级号（0 起）→ 主/子/子子 共 3 层


def children_map(parents):
    """{id: parent_id} → {parent_id: [cid,...]}（无序，调用方自行排序）"""
    m = {}
    for tid, pid in parents.items():
        m.setdefault(pid, []).append(tid)
    return m


def depth_of(parents, tid):
    """节点深度（顶层=0）。环/悬空引用遇即停，返回已走层数。"""
    d, seen, cur = 0, set(), tid
    while parents.get(cur) is not None:
        cur = parents[cur]
        if cur in seen or cur not in parents:
            break
        seen.add(cur)
        d += 1
    return d


def descendants(parents, root):
    """root 的全部后代（不含 root）。防御环。"""
    ch = children_map(parents)
    out, stack = set(), list(ch.get(root, []))
    while stack:
        t = stack.pop()
        if t in out:
            continue
        out.add(t)
        stack.extend(ch.get(t, []))
    return out


def subtree_with_root(parents, root):
    """root 及其全部后代（删除整枝用）"""
    return {root} | descendants(parents, root)


def subtree_height(parents, root):
    """子树层数（单独节点=1）。防御环。"""
    ch = children_map(parents)
    best, stack, seen = 0, [(root, 1)], set()
    while stack:
        t, h = stack.pop()
        if t in seen:
            continue
        seen.add(t)
        best = max(best, h)
        for k in ch.get(t, []):
            stack.append((k, h + 1))
    return best


def can_adopt(parents, new_parent, dragged, max_depth_index=MAX_DEPTH_INDEX):
    """把 dragged（含其子树）挂到 new_parent 下是否合法：
    父存在、不是自己或自己的后代、深度不超限。"""
    if new_parent == dragged:
        return False
    if new_parent is not None and new_parent not in parents:
        return False
    if new_parent in descendants(parents, dragged):
        return False
    new_d = 0 if new_parent is None else depth_of(parents, new_parent) + 1
    return new_d + subtree_height(parents, dragged) - 1 <= max_depth_index


def normalize(parents, max_depth_index=MAX_DEPTH_INDEX):
    """装载自愈：悬空引用/环成员/超深节点的 parent 置 None（拍平为顶层）。
    返回修正后的新 dict，不改入参。"""
    fixed = {t: (p if p is None or p in parents else None)
             for t, p in parents.items()}
    # 环成员拍平：向上游走能回到自身（或步数超界）的节点
    for t in list(fixed):
        seen, cur, steps = set(), t, 0
        while fixed.get(cur) is not None:
            cur = fixed[cur]
            steps += 1
            if cur == t or cur in seen or steps > len(fixed):
                fixed[t] = None
                break
            seen.add(cur)
    # 超深拍平：每轮 BFS 重算深度，只拍平「父不是违规者」的最浅违规层，
    # 其后代深度下一轮自然回落 —— 避免把可保留的子树过度拍平
    for _ in range(len(fixed) + 2):
        ch = children_map(fixed)
        depth = {}
        queue = [(t, 0) for t, p in fixed.items() if p is None]
        while queue:
            t, d = queue.pop()
            if t in depth:
                continue          # 环残留防御
            depth[t] = d
            for k in ch.get(t, []):
                queue.append((k, d + 1))
        violators = {t for t, d in depth.items() if d > max_depth_index}
        if not violators:
            break
        for t in violators:
            if fixed.get(t) is not None and fixed.get(t) not in violators:
                fixed[t] = None
    return fixed


def dfs_order(children_sorted, roots):
    """按给定兄弟序深度优先展开，父必在子前。
    children_sorted: {parent_id: [cid 按显示序]}；roots: 顶层 id 按显示序。"""
    out, stack = [], list(reversed(roots))
    while stack:
        t = stack.pop()
        out.append(t)
        kids = children_sorted.get(t)
        if kids:
            stack.extend(reversed(kids))
    return out
