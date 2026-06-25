"""系统托盘 + 开机自启 —— pystray 跑独立线程，回调投递回 tkinter 主线程"""
import os
import queue
import sys

try:
    import pystray
    from PIL import Image, ImageDraw
    _HAS_TRAY = True
except Exception:
    _HAS_TRAY = False

if sys.platform == "win32":
    import winreg


# HKCU Run 键，用于开机自启
_RUN_KEY = r"Software\Microsoft\Windows\CurrentVersion\Run"
_APP_NAME = "TodoTimer"


def _make_icon_image():
    """程序化生成一个蓝底白钟图标，无需外部图片文件"""
    img = Image.new("RGBA", (64, 64), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    # 圆角蓝底
    d.rounded_rectangle([4, 4, 60, 60], radius=14, fill=(51, 112, 255, 255))
    # 简易时钟：外圈白圆 + 指针
    d.ellipse([18, 18, 46, 46], outline=(255, 255, 255, 255), width=3)
    d.line([32, 32, 32, 24], fill=(255, 255, 255, 255), width=3)  # 时针向上
    d.line([32, 32, 40, 32], fill=(255, 255, 255, 255), width=3)  # 分针向右
    return img


def autostart_enabled():
    """当前是否已设置开机自启"""
    if sys.platform != "win32":
        return False
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, _RUN_KEY, 0, winreg.KEY_READ) as k:
            winreg.QueryValueEx(k, _APP_NAME)
        return True
    except FileNotFoundError:
        return False
    except OSError:
        return False


def set_autostart(enabled):
    """开启/关闭开机自启（写 HKCU Run 键，值为 exe 路径）"""
    if sys.platform != "win32":
        return False
    # exe 路径：打包后用 sys.executable，开发时用 pythonw + main.py
    if getattr(sys, "frozen", False):
        exe_path = f'"{sys.executable}"'
    else:
        exe_path = f'"{sys.executable}" "{os.path.abspath(_main_py())}"'
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, _RUN_KEY, 0,
                            winreg.KEY_SET_VALUE) as k:
            if enabled:
                winreg.SetValueEx(k, _APP_NAME, 0, winreg.REG_SZ, exe_path)
            else:
                try:
                    winreg.DeleteValue(k, _APP_NAME)
                except FileNotFoundError:
                    pass
        return True
    except OSError:
        return False


def _main_py():
    """开发模式下 main.py 的绝对路径"""
    here = os.path.dirname(os.path.abspath(__file__))
    return os.path.normpath(os.path.join(here, "..", "main.py"))


class TrayController:
    """托盘控制器：管理图标生命周期，把菜单动作投递回主线程"""

    def __init__(self, root, on_show, on_quit, on_toggle_autostart):
        """
        root: tkinter Tk
        on_show: 显示窗口回调（在主线程执行）
        on_quit: 退出回调（在主线程执行）
        on_toggle_autostart: 切换自启回调（在主线程执行）
        """
        self.root = root
        self._on_show = on_show
        self._on_quit = on_quit
        self._on_toggle_autostart = on_toggle_autostart
        self._icon = None
        # 线程安全队列：pystray 线程投递回调，主线程 pump() 取出执行。
        # 不能直接 root.after()——从非主线程调用会抛 RuntimeError 并被静默吞掉。
        self._q = queue.Queue()
        if not _HAS_TRAY:
            return

        self._icon = pystray.Icon(
            "TodoTimer",
            _make_icon_image(),
            "待办计时器",
            menu=pystray.Menu(
                pystray.MenuItem("显示窗口", self._act_show, default=True),
                pystray.MenuItem(
                    lambda _: self._autostart_label(),
                    self._act_toggle_autostart),
                pystray.Menu.SEPARATOR,
                pystray.MenuItem("退出", self._act_quit),
            ),
        )
        # 双击托盘图标 → 显示窗口
        self._icon.on_double_click = lambda *_: self._dispatch(self._on_show)

    def _autostart_label(self):
        return "☑ 开机自启" if autostart_enabled() else "☐ 开机自启"

    def _dispatch(self, fn):
        """把回调投递到队列，由主线程 pump() 取出执行（线程安全）。"""
        self._q.put(fn)

    def pump(self):
        """主线程定期调用：取出并执行所有已投递的回调。"""
        while True:
            try:
                fn = self._q.get_nowait()
            except queue.Empty:
                return
            try:
                fn()
            except Exception:
                pass

    def _act_show(self, *_):
        self._dispatch(self._on_show)

    def _act_quit(self, *_):
        self._dispatch(self._on_quit)

    def _act_toggle_autostart(self, *_):
        self._dispatch(self._on_toggle_autostart)

    def start(self):
        if self._icon is not None:
            # pystray.Icon.run() 阻塞，必须在新线程
            import threading
            t = threading.Thread(target=self._icon.run, daemon=True)
            t.start()

    def stop(self):
        if self._icon is not None:
            try:
                self._icon.stop()
            except Exception:
                pass
        self._icon = None

    def notify(self, title, message):
        """托盘气泡通知"""
        if self._icon is not None:
            try:
                self._icon.notify(message, title)
            except Exception:
                pass
