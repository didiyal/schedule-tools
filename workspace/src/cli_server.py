"""本地 CLI/Agent 服务 —— 让外部进程与运行中的 GUI 共享同一份数据状态。

协议: TCP 127.0.0.1:<port>，一连接一请求：一行 JSON 请求 → 一行 JSON 响应。
请求: {"cmd": "list"|"add"|"delete"|..., ...参数}
响应: {"ok": true, ...结果} 或 {"ok": false, "error": "..."}

本模块刻意不依赖 tkinter —— CLI 客户端 (todo_cli.py) 也会导入它解析端口。
所有命令经注入的 post 投递到 tkinter 主线程执行，与 GUI 的渲染/存盘管线
完全一致：Agent 的改动立刻反映到界面，界面的改动对 Agent 立即可见。
"""
import json
import os
import socket
import sys
import threading

# 127.0.0.1 固定端口; 冲突时可用环境变量 TODOTIMER_CLI_PORT 覆盖(客户端同源)
DEFAULT_CLI_PORT = 58372


def resolve_port():
    try:
        return int(os.environ.get("TODOTIMER_CLI_PORT", DEFAULT_CLI_PORT))
    except (TypeError, ValueError):
        return DEFAULT_CLI_PORT


class CLIServer:
    """accept 循环线程 → 每连接一线程 → 请求投递主线程执行 → 回写响应。"""

    def __init__(self, dispatch, post):
        self._dispatch = dispatch   # fn(payload: dict) -> dict，须在主线程执行
        self._post = post           # fn(callable)，把可调用对象投递回主线程
        self._sock = None

    def start(self):
        """绑定并开始监听；端口被占(如开了第二个实例)时仅告警并跳过。"""
        port = resolve_port()
        try:
            s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            s.bind(("127.0.0.1", port))
            s.listen(4)
        except OSError as e:
            print(f"[cli] 端口 {port} 不可用({e})，CLI 服务未启动",
                  file=sys.stderr)
            return
        self._sock = s
        threading.Thread(target=self._accept_loop, daemon=True,
                         name="todo-cli-server").start()

    def _accept_loop(self):
        while True:
            try:
                conn, _ = self._sock.accept()
            except OSError:
                return  # socket 已关闭(应用退出)
            threading.Thread(target=self._serve, args=(conn,),
                             daemon=True).start()

    def _serve(self, conn):
        try:
            conn.settimeout(5)
            buf = b""
            while b"\n" not in buf:
                chunk = conn.recv(4096)
                if not chunk:
                    return
                buf += chunk
            try:
                payload = json.loads(buf.split(b"\n", 1)[0].decode("utf-8"))
                if not isinstance(payload, dict):
                    raise ValueError("请求必须是 JSON 对象")
            except Exception as e:
                resp = {"ok": False, "error": f"请求解析失败: {e}"}
            else:
                resp = self._run_on_main(payload)
            conn.sendall(
                (json.dumps(resp, ensure_ascii=False) + "\n").encode("utf-8"))
        except Exception:
            pass
        finally:
            try:
                conn.close()
            except Exception:
                pass

    def _run_on_main(self, payload, timeout=6.0):
        """把命令投递到主线程同步执行并等待结果。"""
        done = threading.Event()
        result = {}

        def job():
            try:
                result.update(self._dispatch(payload))
            except Exception as e:
                result.update({"ok": False, "error": f"执行异常: {e}"})
            finally:
                done.set()

        self._post(job)
        if not done.wait(timeout):
            return {"ok": False,
                    "error": "主线程响应超时(界面可能被模态对话框阻塞)"}
        return result or {"ok": False, "error": "空响应"}
