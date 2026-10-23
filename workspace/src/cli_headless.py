"""GUI 未运行时的 CLI 落地路径：直接按 GUI 同一套语义读写 data/todos.json。

直接操作原始 dict（不经过 TodoWidget 往返），以保留未知字段、保证向后兼容。
写盘遵循与 MainApp 相同的纪律：.tmp 原子写 → os.replace → .bak 滚动备份。

注意：GUI 运行中时应优先走 IPC（todo_cli.py 已处理）；仅当连接被拒绝
(无监听) 时才允许写文件，否则改动会被 GUI 的下一次存盘覆盖。
"""
import json
import os
import time

from . import tree
from .utils import fmt_time, get_today_str


def load_raw(path):
    """读取数据文件，返回顶层 dict；兼容旧版纯数组格式。不存在则返回 None。"""
    if not os.path.exists(path):
        return None
    with open(path, "r", encoding="utf-8") as f:
        raw = json.load(f)
    if isinstance(raw, list):  # 旧版数组格式
        raw = {"todos": raw}
    raw.setdefault("todos", [])
    return raw


def save_raw(path, raw):
    """原子写 + .bak 滚动备份（与 MainApp._save 同纪律）。"""
    os.makedirs(os.path.dirname(path) or ".", exist_ok=True)
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(raw, f, ensure_ascii=False, indent=2)
    os.replace(tmp, path)
    try:
        with open(path, "r", encoding="utf-8") as src:
            with open(path + ".bak", "w", encoding="utf-8") as dst:
                dst.write(src.read())
    except Exception:
        pass


def brief(item, parents=None):
    """单条待办的对外摘要（与 IPC list 输出同构，单位：秒）。
    parents 提供时附带 depth（{id: parent_id}，宜先经 tree.normalize）。"""
    elapsed = item.get("elapsed", 0)
    if item.get("running") and item.get("last_started_at"):
        elapsed += int(time.time() - item["last_started_at"])
    if item.get("completed"):
        status = "completed"
    elif item.get("running"):
        status = "running"
    else:
        status = "paused"
    out = {
        "id": str(item.get("id")),
        "text": item.get("text", ""),
        "date": item.get("date", get_today_str()),
        "elapsed_seconds": elapsed,
        "elapsed_hms": fmt_time(elapsed),
        "status": status,
        "starred": bool(item.get("starred")),
        "in_panel": bool(item.get("visible")),
        "target_seconds": item.get("target_seconds"),
        "parent": item.get("parent"),
    }
    if parents is not None:
        out["depth"] = tree.depth_of(parents, str(item.get("id")))
    return out


def _live_elapsed(item):
    return brief(item)["elapsed_seconds"]


def apply(payload, raw):
    """在 raw(dict) 上执行写命令，返回响应 dict。只处理写命令，list 走 brief。"""
    cmd = payload.get("cmd")
    todos = raw["todos"]
    today = get_today_str()
    parents = {str(it.get("id")): it.get("parent") for it in todos}

    def get_item(pid):
        for it in todos:
            if str(it.get("id")) == str(pid):
                return it
        raise ValueError(f"id 不存在: {pid}")

    if cmd == "add":
        text = (payload.get("text") or "").strip()
        if not text:
            return {"ok": False, "error": "text 不能为空"}
        parent = payload.get("parent")
        if parent in (None, "", "top", "null"):
            parent = None
        else:
            parent = str(parent)
            if parent not in parents:
                return {"ok": False, "error": f"父任务不存在: {parent}"}
            if not tree.can_adopt(parents, parent, "__new__"):
                return {"ok": False, "error": "超过最大层级（3 层）"}
        try:
            next_id = max(int(it.get("id", 0)) for it in todos) + 1
        except ValueError:
            next_id = 1
        try:
            next_order = max(it.get("order", 0) for it in todos) + 1
        except ValueError:
            next_order = 0
        item = {
            "id": str(next_id), "text": text, "parent": parent,
            "elapsed": 0,
            "completed": False, "x": 0, "y": 0, "visible": False,
            "order": next_order, "starred": bool(payload.get("starred")),
            "date": payload.get("date") or today,
            "running": False, "last_started_at": None,
            "target_seconds": None, "reminded": False,
        }
        todos.append(item)
        parents[str(next_id)] = parent
        return {"added": brief(item, tree.normalize(parents))}

    if cmd == "delete":
        item = get_item(payload.get("id"))
        pid = str(item.get("id"))
        branch = tree.subtree_with_root(parents, pid)   # 删整枝
        todos[:] = [it for it in todos if str(it.get("id")) not in branch]
        return {"deleted": pid, "count": len(branch)}

    if cmd == "move":
        item = get_item(payload.get("id"))
        date = payload.get("date")
        if not date:
            return {"ok": False, "error": "缺少 date (YYYY-MM-DD)"}
        item["date"] = date
        return {"moved": str(item.get("id")), "date": date}

    if cmd == "reparent":
        item = get_item(payload.get("id"))
        pid = payload.get("parent")
        if pid in (None, "", "top", "null"):
            pid = None
        else:
            pid = str(pid)
        tid = str(item.get("id"))
        if not tree.can_adopt(parents, pid, tid):
            return {"ok": False,
                    "error": "非法移动：目标不存在/成环/超过 3 层"}
        item["parent"] = pid
        parents[tid] = pid
        return {"reparented": tid, "parent": pid,
                "depth": tree.depth_of(tree.normalize(parents), tid)}

    if cmd == "done":
        item = get_item(payload.get("id"))
        item["completed"] = True
        item["date"] = today          # 与 TodoWidget.set_complete 语义一致
        item["running"] = False
        item["last_started_at"] = None
        return {"done": str(item.get("id"))}

    if cmd == "undo":
        item = get_item(payload.get("id"))
        item["completed"] = False
        return {"undone": str(item.get("id"))}

    if cmd == "rename":
        item = get_item(payload.get("id"))
        text = (payload.get("text") or "").strip()
        if not text:
            return {"ok": False, "error": "text 不能为空"}
        item["text"] = text
        return {"renamed": str(item.get("id"))}

    if cmd == "star":
        item = get_item(payload.get("id"))
        item["starred"] = bool(payload.get("starred", not item.get("starred")))
        return {"id": str(item.get("id")), "starred": item["starred"]}

    if cmd in ("start", "pause"):
        item = get_item(payload.get("id"))
        if item.get("completed"):
            return {"ok": False, "error": "已完成的待办不能计时"}
        if cmd == "start":
            if not item.get("running"):
                item["running"] = True
                item["last_started_at"] = time.time()
        elif item.get("running"):
            item["elapsed"] = _live_elapsed(item)
            item["running"] = False
            item["last_started_at"] = None
        return {"id": str(item.get("id")), "status": brief(item)["status"]}

    return {"ok": False, "error": f"未知命令: {cmd!r}"}


def filter_items(raw, date=None, show_all=False, date_from=None,
                 date_to=None, search=None):
    """按 list/stats 共用规则筛出待办 dict 列表，返回 (scope标签, [dict])。"""
    todos = raw["todos"]
    if show_all:
        items = list(todos)
        scope = "all"
    elif date_from or date_to:
        lo, hi = date_from or "0000-01-01", date_to or "9999-12-31"
        items = [it for it in todos if lo <= it.get("date", today_str()) <= hi]
        scope = f"{lo}~{hi}"
    else:
        d = date or today_str()
        items = [it for it in todos if it.get("date", today_str()) == d]
        scope = d
    q = (search or "").strip().lower()
    if q:
        items = [it for it in items if q in it.get("text", "").lower()]
    return scope, items


def list_items(raw, date=None, show_all=False, date_from=None,
               date_to=None, search=None):
    """返回 (scope, [brief...])；排序为 DFS 森林序（日组倒序，重点→order），
    与 GUI 显示顺序一致，父必在子前。"""
    scope, items = filter_items(raw, date, show_all, date_from, date_to,
                                search)
    parents = tree.normalize(
        {str(it.get("id")): it.get("parent") for it in raw["todos"]})
    wanted = {str(it.get("id")) for it in items}
    by_id = {str(it.get("id")): it for it in items}
    # 子集外的中继父视同顶层，保证输出父先子后
    sub_parents = {t: (parents.get(t) if parents.get(t) in wanted else None)
                   for t in wanted}
    ch = tree.children_map(sub_parents)

    def key(t):
        it = by_id[t]
        return (not it.get("starred", False), it.get("order", 0))

    children_sorted = {p: sorted(ids, key=key) for p, ids in ch.items()}
    roots_by_date = {}
    for t in wanted:
        if sub_parents[t] is None:
            d = by_id[t].get("date") or today_str()
            roots_by_date.setdefault(d, []).append(t)
    ordered = []
    for d in sorted(roots_by_date.keys(), reverse=True):
        ordered.extend(tree.dfs_order(
            children_sorted, sorted(roots_by_date[d], key=key)))
    return scope, [brief(by_id[t], parents) for t in ordered]


def stats_items(raw, date=None, show_all=False, date_from=None,
                date_to=None, search=None, top=5):
    """耗时/数量统计：总数、状态分布、累计投入、耗时 Top N。"""
    scope, items = filter_items(raw, date, show_all, date_from, date_to,
                                search)
    elapsed = [(it, _live_elapsed(it)) for it in items]
    total = sum(e for _, e in elapsed)
    running = [it for it in items if it.get("running")
               and not it.get("completed")]
    ranked = sorted(elapsed, key=lambda ie: -ie[1])[:top]
    return {
        "scope": scope, "count": len(items),
        "count_completed": sum(1 for it in items if it.get("completed")),
        "count_running": len(running),
        "total_elapsed_seconds": total,
        "total_elapsed_hms": fmt_time(total),
        "running_now": [{"id": str(it.get("id")), "text": it.get("text", "")}
                        for it in running],
        "top": [{"id": str(it.get("id")), "text": it.get("text", ""),
                 "elapsed_seconds": e, "elapsed_hms": fmt_time(e)}
                for it, e in ranked],
    }


def today_str():
    return get_today_str()
