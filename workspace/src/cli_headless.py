"""GUI 未运行时的 CLI 落地路径：直接按 GUI 同一套语义读写 data/todos.json。

直接操作原始 dict（不经过 TodoWidget 往返），以保留未知字段、保证向后兼容。
写盘遵循与 MainApp 相同的纪律：.tmp 原子写 → os.replace → .bak 滚动备份。

注意：GUI 运行中时应优先走 IPC（todo_cli.py 已处理）；仅当连接被拒绝
(无监听) 时才允许写文件，否则改动会被 GUI 的下一次存盘覆盖。
"""
import json
import os
import time

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


def brief(item):
    """单条待办的对外摘要（与 IPC list 输出同构，单位：秒）。"""
    elapsed = item.get("elapsed", 0)
    if item.get("running") and item.get("last_started_at"):
        elapsed += int(time.time() - item["last_started_at"])
    if item.get("completed"):
        status = "completed"
    elif item.get("running"):
        status = "running"
    else:
        status = "paused"
    return {
        "id": str(item.get("id")),
        "text": item.get("text", ""),
        "date": item.get("date", get_today_str()),
        "elapsed_seconds": elapsed,
        "elapsed_hms": fmt_time(elapsed),
        "status": status,
        "starred": bool(item.get("starred")),
        "in_panel": bool(item.get("visible")),
        "target_seconds": item.get("target_seconds"),
    }


def _live_elapsed(item):
    return brief(item)["elapsed_seconds"]


def apply(payload, raw):
    """在 raw(dict) 上执行写命令，返回响应 dict。只处理写命令，list 走 brief。"""
    cmd = payload.get("cmd")
    todos = raw["todos"]
    today = get_today_str()

    def get_item(pid):
        for it in todos:
            if str(it.get("id")) == str(pid):
                return it
        raise ValueError(f"id 不存在: {pid}")

    if cmd == "add":
        text = (payload.get("text") or "").strip()
        if not text:
            return {"ok": False, "error": "text 不能为空"}
        try:
            next_id = max(int(it.get("id", 0)) for it in todos) + 1
        except ValueError:
            next_id = 1
        try:
            next_order = max(it.get("order", 0) for it in todos) + 1
        except ValueError:
            next_order = 0
        item = {
            "id": str(next_id), "text": text, "elapsed": 0,
            "completed": False, "x": 0, "y": 0, "visible": False,
            "order": next_order, "starred": bool(payload.get("starred")),
            "date": payload.get("date") or today,
            "running": False, "last_started_at": None,
            "target_seconds": None, "reminded": False,
        }
        todos.append(item)
        return {"added": brief(item)}

    if cmd == "delete":
        item = get_item(payload.get("id"))
        todos.remove(item)
        return {"deleted": str(item.get("id"))}

    if cmd == "move":
        item = get_item(payload.get("id"))
        date = payload.get("date")
        if not date:
            return {"ok": False, "error": "缺少 date (YYYY-MM-DD)"}
        item["date"] = date
        return {"moved": str(item.get("id")), "date": date}

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


def list_items(raw, date=None, show_all=False):
    """返回 (scope, [brief...])；排序与 GUI 一致：未完成在前、重点优先。"""
    todos = raw["todos"]
    if show_all:
        items = list(todos)
        scope = "all"
    else:
        d = date or today_str()
        items = [it for it in todos if it.get("date", today_str()) == d]
        scope = d
    items.sort(key=lambda it: (bool(it.get("completed")),
                               not it.get("starred", False),
                               it.get("order", 0)))
    return scope, [brief(it) for it in items]


def today_str():
    return get_today_str()
