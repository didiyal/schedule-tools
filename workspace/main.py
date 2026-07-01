"""待办计时器 - 入口"""
# DPI 感知由 customtkinter 在创建 CTk() 时统一设置，此处不再手动调用
# SetProcessDpiAwareness，避免双重缩放导致模糊/过大（见 spec §8.1）。
from src.main_app import MainApp

if __name__ == "__main__":
    app = MainApp()
    app.run()
