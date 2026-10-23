"""待办计时器 命令行入口 —— 供 AI Agent / 脚本读写待办数据。

GUI 运行中时命令经本地 IPC 发给 GUI 执行（改动立刻显示在界面）；
GUI 未运行时降级为直接读写 data/todos.json（同语义、原子写）。

用法:
  python todo_cli.py                          # 列出今日待办
  python todo_cli.py list [--date YYYY-MM-DD | --all] [--json]
  python todo_cli.py add "任务文本" [--date YYYY-MM-DD] [--star] [--json]
  python todo_cli.py delete <id> [--json]
  python todo_cli.py done <id> | undo <id> | start <id> | pause <id>
  python todo_cli.py move <id> <YYYY-MM-DD>
  python todo_cli.py rename <id> "新文本"
  python todo_cli.py star <id> [--off]

打包版等价入口: 待办计时器.exe cli <同上参数>
"""
import argparse
import json
import socket
import sys

from src.cli_server import resolve_port
from src.cli_headless import apply as headless_apply
from src.cli_headless import list_items as headless_list
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
    if payload["cmd"] == "list":
        scope, items = headless_list(raw, payload.get("date"),
                                     bool(payload.get("all")))
        return {"ok": True, "scope": scope, "count": len(items),
                "todos": items}
    resp = headless_apply(payload, raw)
    if resp.get("ok", True):
        save_raw(SAVE_FILE, raw)
    return resp


# ── 输出 ─────────────────────────────────────────────────
STATUS_CN = {"running": "运行中", "paused": "暂停", "completed": "已完成"}


def print_list(resp):
    scope = resp["scope"]
    label = "全部待办" if scope == "all" else f"待办 ({scope})"
    print(f"{label} — {resp['count']} 项")
    for it in resp["todos"]:
        mark = "★ " if it["starred"] else ""
        print(f"  #{it['id']:<4} [{STATUS_CN[it['status']]}] {mark}"
              f"{it['text']}  {it['elapsed_hms']}")


def print_result(resp, args):
    if args.json:
        print(json.dumps(resp, ensure_ascii=False, indent=2))
        return
    if not resp.get("ok", True):
        print(f"错误: {resp.get('error')}", file=sys.stderr)
        sys.exit(1)
    if resp["cmd"] == "list":
        print_list(resp)
        return
    # 写命令的人性化回显
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

    p = sub.add_parser("list", help="列出待办 (默认今日)")
    p.add_argument("--date", default=None, help="按日期筛选 (YYYY-MM-DD)")
    p.add_argument("--all", action="store_true", help="列出全部待办")
    p.add_argument("--json", action="store_true", help="JSON 输出")

    p = sub.add_parser("add", help="新增待办")
    p.add_argument("text", help="任务文本")
    p.add_argument("--date", default=None, help="日期 (默认今天)")
    p.add_argument("--star", action="store_true", help="标为重点")
    p.add_argument("--json", action="store_true")

    for name, help_ in (("delete", "删除待办"), ("done", "标记完成"),
                        ("undo", "取消完成"), ("start", "开始计时"),
                        ("pause", "暂停计时"), ("star", "切换/设置重点")):
        p = sub.add_parser(name, help=help_)
        p.add_argument("id", help="待办 id")
        if name == "star":
            p.add_argument("--off", action="store_true", help="取消重点")
            p.add_argument("--on", action="store_true", help="设为重点")
        p.add_argument("--json", action="store_true")

    p = sub.add_parser("move", help="改期")
    p.add_argument("id")
    p.add_argument("date", help="目标日期 (YYYY-MM-DD)")
    p.add_argument("--json", action="store_true")

    p = sub.add_parser("rename", help="重命名")
    p.add_argument("id")
    p.add_argument("text", help="新文本")
    p.add_argument("--json", action="store_true")

    return ap


def payload_from_args(args):
    cmd = args.cmd
    p = {"cmd": cmd}
    if cmd == "list":
        if args.all:
            p["all"] = True
        elif args.date:
            p["date"] = get_today_str() if args.date == "today" else args.date
    elif cmd == "add":
        p["text"] = args.text
        if args.date:
            p["date"] = get_today_str() if args.date == "today" else args.date
        p["starred"] = args.star
    else:
        p["id"] = args.id
        if cmd == "move":
            p["date"] = args.date
        elif cmd == "rename":
            p["text"] = args.text
        elif cmd == "star" and (args.off or args.on):
            p["starred"] = bool(args.on)
    return p


def main(argv=None):
    ap = build_parser()
    args = ap.parse_args(argv)
    if not args.cmd:
        args.cmd = "list"          # 无子命令 → 默认列出今日
    payload = payload_from_args(args)
    payload["cmd"] = args.cmd
    resp = execute(payload, args)
    resp.setdefault("cmd", args.cmd)
    print_result(resp, args)
    return 0 if resp.get("ok", True) else 1


if __name__ == "__main__":
    sys.exit(main())
