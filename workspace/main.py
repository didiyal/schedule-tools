"""待办计时器 - 入口"""
import ctypes
import sys

if sys.platform == "win32":
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)  # Per-monitor DPI
    except Exception:
        try:
            ctypes.windll.user32.SetProcessDPIAware()
        except Exception:
            pass

from src.main_app import MainApp

if __name__ == "__main__":
    app = MainApp()
    app.run()
