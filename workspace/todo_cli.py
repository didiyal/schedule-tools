"""待办计时器 命令行入口 —— 供 AI Agent / 脚本读写待办数据。

GUI 运行中时命令经本地 IPC 发给 GUI 执行（改动立刻显示在界面）；
GUI 未运行时降级为直接读写 data/todos.json（同语义、原子写）。

用法:
  python todo_cli.py                                   # 列出今日待办
  python todo_cli.py list [--date YYYY-MM-DD | --all |
                           --from YYYY-MM-DD --to YYYY-MM-DD]
                         [--search 关键词] [--json]
  python todo_cli.py search 关键词 [--json]            # 跨全部日期搜索
  python todo_cli.py stats [--date ... | --all | --from ... --to ...]
                          [--search 关键词] [--top N] [--json]
  python todo_cli.py add "任务文本" [--date YYYY-MM-DD] [--star] [--json]
  python todo_cli.py delete <id> [id ...] [--json]     # 支持批量
  python todo_cli.py done <id> [id ...] | undo <id> [id ...]
  python todo_cli.py start <id> [id ...] | pause <id> [id ...]
  python todo_cli.py move <id> <YYYY-MM-DD>
  python todo_cli.py rename <id> "新文本"
  python todo_cli.py star <id> [--on | --off]

打包版等价入口: 待办计时器.exe cli <同上参数>
"""
import argparse
import json
import socket
import sys

from src.cli_server import resolve_port
from src.cli_headless import apply as headless_apply
from src.cli_headless import list_items as headless_list
from src.cli_headless import stats_items as headless_stats
from src.cli_headless import load_raw, save_raw
from src.utils import get_today_str


# ── IPC 客户端 ────────────────────────────────────────────
def send_ipc(payload, timeout=4.0):
    """连接运行中的 GUI 执行命令。连接被拒绝时抛 ConnectionRefusedError。"""
    sock = socket.create_connection(("127.0.0.1", resolve_port()),
                                    timeout=timeout)
    try:
        sock.sendall((json.dumps(payload, ensure_ascii=False) + "\n")
                     .encode("utf-8"))
        buf = b""
        while b"\n" not in buf:
            chunk = sock.recv(4096)
            if not chunk:
                break
            buf += chunk
    finally:
        sock.close()
    if not buf:
        raise ConnectionError("GUI 无响应")
    return json.loads(buf.decode("utf-8"))


def execute(payload, args):
    """执行一条命令：GUI 在线走 IPC，否则降级直写文件。返回响应 dict。"""
    if args.port:
        import os
        os.environ["TODOTIMER_CLI_PORT"] = str(args.port)
    if not args.headless:
        try:
            return send_ipc(payload)
        except ConnectionRefusedError:
            pass  # GUI 未运行 → 降级
        except (socket.timeout, ConnectionError) as e:
            return {"ok": False,
                    "error": f"GUI 在线但无响应({e})，未执行任何写操作"}
        except OSError as e:
            return {"ok": False, "error": f"IPC 连接失败: {e}"}

    # ── headless: 直接读写 JSON ──
    from src.main_app import SAVE_FILE
    raw = load_raw(SAVE_FILE)
    if raw is None:
        raw = {"todos": []}
    cmd = payload["cmd"]
    if cmd == "list":
        scope, items = headless_list(raw, payload.get("date"),
                                     bool(payload.get("all")),
                                     payload.get("date_from"),
                                     payload.get("date_to"),
                                     payload.get("search"))
        return {"ok": True, "scope": scope, "count": len(items),
                "search": (payload.get("search") or None), "todos": items}
    if cmd == "stats":
        resp = headless_stats(raw, payload.get("date"),
                              bool(payload.get("all")),
                              payload.get("date_from"),
                              payload.get("date_to"),
                              payload.get("search"),
                              payload.get("top") or 5)
        resp["ok"] = True
        return resp
    # 写命令（GUI 侧批量 id 在服务端循环；headless 在此循环，写盘一次）
    try:
        resp = headless_apply(payload, raw)
    except Exception as e:
        return {"ok": False, "error": str(e)}
    if resp.get("ok", True):
        save_raw(SAVE_FILE, raw)
    return resp


def execute_batch(verb, ids, args, extra=None):
    """批量执行 id 类写命令。多 id 且 GUI 在线时合成一次请求；
    headless 时逐条 apply、最后统一落盘。返回聚合响应。"""
    ids = [str(i) for i in ids]
    if len(ids) == 1:
        payload = {"cmd": verb, "id": ids[0]}
        payload.update(extra or {})
        return execute(payload, args)
    if not args.headless:
        payload = {"cmd": verb, "id": ids}
        payload.update(extra or {})
        return execute(payload, args)
    # headless 批量
    from src.main_app import SAVE_FILE
    raw = load_raw(SAVE_FILE)
    if raw is None:
        raw = {"todos": []}
    results, failed = [], []
    for i in ids:
        try:
            resp = headless_apply({"cmd": verb, "id": i, **(extra or {})}, raw)
        except Exception as e:
            resp = {"ok": False, "error": str(e)}
        results.append(resp)
        if not resp.get("ok", True):
            failed.append(f"#{i}: {resp.get('error')}")
    if any(r.get("ok", True) for r in results):
        # 部分成功也持久化成功的部分(与 GUI 批量路径一致), 否则会出现
        # "回显已删除、重启又回来"的幽灵状态
        save_raw(SAVE_FILE, raw)
    return {"ok": not failed, "results": results,
            "errors": failed or None}


# ── 输出 ─────────────────────────────────────────────────
STATUS_CN = {"running": "运行中", "paused": "暂停", "completed": "已完成"}


def print_list(resp):
    scope = resp["scope"]
    label = "全部待办" if scope == "all" else f"待办 ({scope})"
    suffix = f" · 关键词「{resp['search']}」" if resp.get("search") else ""
    print(f"{label} — {resp['count']} 项{suffix}")
    for it in resp["todos"]:
        mark = "★ " if it["starred"] else ""
        print(f"  #{it['id']:<4} [{STATUS_CN[it['status']]}] {mark}"
              f"{it['text']}  {it['elapsed_hms']}")


def print_stats(resp):
    print(f"统计 ({resp['scope']}) — 共 {resp['count']} 项: "
          f"运行中 {resp['count_running']} · 已完成 {resp['count_completed']} "
          f"· 其他 {resp['count'] - resp['count_running'] - resp['count_completed']}")
    print(f"累计投入 {resp['total_elapsed_hms']}")
    if resp["running_now"]:
        now = "、".join(f"#{r['id']} {r['text']}" for r in resp["running_now"])
        print(f"正在计时: {now}")
    if resp["top"]:
        print(f"耗时 Top {len(resp['top'])}:")
        for it in resp["top"]:
            print(f"  #{it['id']:<4} {it['text']}  {it['elapsed_hms']}")


def print_result(resp, args):
    if args.json:
        print(json.dumps(resp, ensure_ascii=False, indent=2))
        return
    if not resp.get("ok", True):
        detail = resp.get("errors") or resp.get("error")
        print(f"错误: {detail}", file=sys.stderr)
        return
    cmd = resp.get("cmd")
    if cmd == "list":
        print_list(resp)
        return
    if cmd == "stats":
        print_stats(resp)
        return
    results = resp.get("results")
    if results is not None:
        result_cn = {"deleted": "已删除", "done": "已标记完成",
                     "undone": "已取消完成"}
        for r in results:
            if not r.get("ok", True):
                print(f"  失败: {r.get('error')}", file=sys.stderr)
                continue
            for key in ("deleted", "done", "undone"):
                if key in r:
                    print(f"  #{r[key]} {result_cn[key]}")
            if "id" in r and "status" in r:
                print(f"  #{r['id']} 状态: {STATUS_CN.get(r['status'], r['status'])}")
            elif "starred" in r:
                print(f"  #{r['id']} {'已标为重点' if r['starred'] else '已取消重点'}")
        return
    # 单条写命令的人性化回显
    if "added" in resp:
        it = resp["added"]
        print(f"已添加 #{it['id']} {it['text']} (日期 {it['date']})")
    elif "deleted" in resp:
        print(f"已删除 #{resp['deleted']}")
    elif "moved" in resp:
        print(f"#{resp['moved']} 已改期至 {resp['date']}")
    elif "done" in resp:
        print(f"#{resp['done']} 已标记完成")
    elif "undone" in resp:
        print(f"#{resp['undone']} 已取消完成")
    elif "renamed" in resp:
        print(f"#{resp['renamed']} 已改名为: {resp['text']}")
    elif "starred" in resp:
        state = "已标为重点" if resp["starred"] else "已取消重点"
        print(f"#{resp['id']} {state}")
    elif "status" in resp:
        print(f"#{resp['id']} 状态: {STATUS_CN.get(resp['status'], resp['status'])}")
    else:
        print(json.dumps(resp, ensure_ascii=False))


# ── 参数解析 ─────────────────────────────────────────────
def _add_scope_args(p, with_search=True):
    p.add_argument("--date", default=None, help="按日期筛选 (YYYY-MM-DD | today)")
    p.add_argument("--all", action="store_true", help="不限日期")
    p.add_argument("--from", dest="date_from", default=None,
                   help="日期范围起 (YYYY-MM-DD，与 --to 连用)")
    p.add_argument("--to", dest="date_to", default=None, help="日期范围止")
    if with_search:
        p.add_argument("--search", "-s", default=None, help="关键词过滤")


def _add_json(p):
    p.add_argument("--json", action="store_true", help="JSON 输出")


def _add_ids(p, help_="待办 id（可多个，批量操作）"):
    p.add_argument("ids", nargs="+", help=help_)


def build_parser():
    ap = argparse.ArgumentParser(
        prog="todo_cli",
        description="待办计时器 命令行（GUI 运行中走 IPC 实时同步，"
                    "未运行时直接读写 data/todos.json）")
    ap.add_argument("--headless", action="store_true",
                    help="跳过 IPC，强制直接读写 JSON 文件")
    ap.add_argument("--port", type=int, default=None,
                    help="IPC 端口 (默认 58372 或环境变量 TODOTIMER_CLI_PORT)")
    sub = ap.add_subparsers(dest="cmd")

    p = sub.add_parser("list", help="列出待办 (默认今日，可搜索/范围)")
    _add_scope_args(p)
    _add_json(p)

    p = sub.add_parser("search", help="跨全部日期的关键词搜索")
    p.add_argument("query", help="关键词 (对任务文本做不区分大小写的子串匹配)")
    _add_json(p)

    p = sub.add_parser("stats", help="耗时/数量统计")
    _add_scope_args(p)
    p.add_argument("--top", type=int, default=5, help="耗时排行条数 (默认 5)")
    _add_json(p)

    p = sub.add_parser("add", help="新增待办")
    p.add_argument("text", help="任务文本")
    p.add_argument("--date", default=None, help="日期 (默认今天)")
    p.add_argument("--star", action="store_true", help="标为重点")
    _add_json(p)

    for name, help_ in (("delete", "删除待办"), ("done", "标记完成"),
                        ("undo", "取消完成"), ("start", "开始计时"),
                        ("pause", "暂停计时"), ("star", "设置/取消重点")):
        p = sub.add_parser(name, help=help_)
        _add_ids(p)
        if name == "star":
            p.add_argument("--off", action="store_true", help="取消重点")
            p.add_argument("--on", action="store_true", help="设为重点")
        _add_json(p)

    p = sub.add_parser("move", help="改期")
    p.add_argument("id")
    p.add_argument("date", help="目标日期 (YYYY-MM-DD)")
    _add_json(p)

    p = sub.add_parser("rename", help="重命名")
    p.add_argument("id")
    p.add_argument("text", help="新文本")
    _add_json(p)

    return ap


def _norm_date(s):
    return get_today_str() if s == "today" else s


def payload_from_args(args):
    cmd = args.cmd
    p = {"cmd": cmd}
    if cmd == "list":
        if args.all:
            p["all"] = True
        elif args.date:
            p["date"] = _norm_date(args.date)
        if args.date_from:
            p["date_from"] = _norm_date(args.date_from)
        if args.date_to:
            p["date_to"] = _norm_date(args.date_to)
        if args.search:
            p["search"] = args.search
    elif cmd == "search":
        p["cmd"] = "list"
        p["all"] = True
        p["search"] = args.query
    elif cmd == "stats":
        if args.all:
            p["all"] = True
        elif args.date:
            p["date"] = _norm_date(args.date)
        if args.date_from:
            p["date_from"] = _norm_date(args.date_from)
        if args.date_to:
            p["date_to"] = _norm_date(args.date_to)
        if args.search:
            p["search"] = args.search
        p["top"] = args.top
    elif cmd == "add":
        p["text"] = args.text
        if args.date:
            p["date"] = _norm_date(args.date)
        p["starred"] = args.star
    elif cmd == "move":
        p["id"] = args.id
        p["date"] = args.date
    elif cmd == "rename":
        p["id"] = args.id
        p["text"] = args.text
    elif cmd == "star":
        p["ids"] = args.ids
        if args.off or args.on:
            p["starred"] = bool(args.on)
    else:  # delete/done/undo/start/pause
        p["ids"] = args.ids
    return p


def main(argv=None):
    ap = build_parser()
    args = ap.parse_args(argv)
    if not args.cmd:
        args.cmd = "list"          # 无子命令 → 默认列出今日
    payload = payload_from_args(args)
    payload["cmd"] = args.cmd if args.cmd != "search" else "list"

    if "ids" in payload:           # 批量 id 写命令
        verb = payload["cmd"]
        extra = {"starred": payload["starred"]} if "starred" in payload else {}
        resp = execute_batch(verb, payload["ids"], args, extra)
    else:
        resp = execute(payload, args)
    resp.setdefault("ok", True)
    resp.setdefault("cmd", "list" if args.cmd == "search" else args.cmd)
    print_result(resp, args)
    return 0 if resp.get("ok", True) else 1


if __name__ == "__main__":
    sys.exit(main())
