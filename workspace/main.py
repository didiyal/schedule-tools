"""待办计时器 - 入口

待办计时器.exe cli ...  → 命令行模式(供 AI Agent/脚本读写待办，
GUI 运行中走 IPC 实时同步，未运行时直接读写 data/todos.json)。
"""
# DPI 感知由 customtkinter 在创建 CTk() 时统一设置，此处不再手动调用
# SetProcessDpiAwareness，避免双重缩放导致模糊/过大（见 spec §8.1）。
import sys


def main():
    if len(sys.argv) > 1 and sys.argv[1] == "cli":
        import todo_cli
        return todo_cli.main(sys.argv[2:])
    from src.main_app import MainApp
    app = MainApp()
    app.run()
    return 0


if __name__ == "__main__":
    sys.exit(main())
