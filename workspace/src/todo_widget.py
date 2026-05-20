import tkinter as tk
from .constants import C, FONT
from .utils import flat_btn, fmt_time
from .tooltip import ToolTip


class TodoWidget:
    """桌面悬浮待办窗口"""

    def __init__(self, todo_id, text, elapsed=0, completed=False,
                 master_root=None, order=0, starred=False):
        self.todo_id = todo_id
        self.text = text
        self.elapsed = elapsed
        self.running = False
        self.completed = completed
        self._after_id = None
        self._root = master_root
        self._order = order
        self.starred = starred

        self.win = tk.Toplevel(master_root)
        self.win.overrideredirect(True)
        self.win.attributes("-topmost", True)
        self.win.geometry("280x125")

        shadow = tk.Frame(self.win, bg=C["shadow"], bd=0)
        shadow.pack(fill=tk.BOTH, expand=True)

        self.card = tk.Frame(shadow, bg=C["surface"], bd=0)
        self.card.pack(fill=tk.BOTH, expand=True, padx=2, pady=2)

        # ── 标题栏 ──
        self.top = tk.Frame(self.card, bg=C["bg"], height=28)
        self.top.pack(fill=tk.X, side=tk.TOP)
        self.top.pack_propagate(False)

        display_text = self._truncate(self.text, 16)

        self.title_lbl = tk.Label(
            self.top, text=display_text, bg=C["bg"], fg=C["text"],
            font=(FONT, 10, "bold"), anchor="w", padx=8,
        )
        self.title_lbl.pack(side=tk.LEFT, fill=tk.X, expand=True)

        ToolTip(self.title_lbl, lambda: self.text)

        # 完成状态标记
        self.done_mark = tk.Label(
            self.top, text="", bg=C["bg"], fg=C["green"],
            font=(FONT, 10, "bold"), padx=4,
        )
        self.done_mark.pack(side=tk.RIGHT)

        # 关闭按钮
        self.close_lbl = tk.Label(
            self.top, text="✕", bg=C["bg"], fg=C["text_ter"],
            font=(FONT, 12), cursor="hand2", padx=4,
        )
        self.close_lbl.pack(side=tk.RIGHT)
        self.close_lbl.bind("<Button-1>", self._on_close_click)
        self.close_lbl.bind("<Enter>",
                            lambda e: self.close_lbl.configure(fg=C["text"], bg=C["border"]))
        self.close_lbl.bind("<Leave>",
                            lambda e: self.close_lbl.configure(fg=C["text_ter"], bg=C["bg"]))

        # ── 时间 ──
        self.time_lbl = tk.Label(
            self.card, text=fmt_time(self.elapsed),
            bg=C["surface"], fg=C["text"],
            font=("Consolas", 32, "bold"),
        )
        self.time_lbl.pack(pady=(8, 6))

        # ── 按钮 ──
        btns = tk.Frame(self.card, bg=C["surface"])
        btns.pack(pady=(0, 8))

        self.btn_start = flat_btn(btns, "▶", self.start,
                                  C["green"], C["green_hover"],
                                  font_size=10, pady=3, padx=12)
        self.btn_start.pack(side=tk.LEFT, padx=2)

        self.btn_pause = flat_btn(btns, "⏸", self.pause,
                                  C["orange"], C["orange_hover"],
                                  font_size=10, pady=3, padx=12)
        self.btn_pause.pack(side=tk.LEFT, padx=2)

        self.btn_reset = flat_btn(btns, "↺", self.reset,
                                  C["text_sec"], "#999",
                                  font_size=10, pady=3, padx=12)
        self.btn_reset.pack(side=tk.LEFT, padx=2)

        # ── 拖拽 ──
        for w in (self.top, self.title_lbl):
            w.bind("<Button-1>", self._drag_start)
            w.bind("<B1-Motion>", self._drag_move)

        # ── 右键菜单 ──
        self._build_menu()

        self.title_lbl.bind("<Double-Button-1>", self._on_close_click)
        self.win.protocol("WM_DELETE_WINDOW", self._on_close_click)

        if self.completed:
            self._apply_completed_style()

    def _build_menu(self):
        self.menu = tk.Menu(self.win, tearoff=0,
                            bg=C["surface"], fg=C["text"],
                            activebackground=C["blue"], activeforeground="#FFFFFF",
                            font=(FONT, 10))
        if self.completed:
            self.menu.add_command(label="↩ 取消完成", command=self._toggle_complete)
        else:
            self.menu.add_command(label="✓ 标记完成", command=self._toggle_complete)
        if self.starred:
            self.menu.add_command(label="☆ 取消重点", command=self._toggle_star)
        else:
            self.menu.add_command(label="★ 标为重点", command=self._toggle_star)
        self.menu.add_separator()
        self.menu.add_command(label="开始计时", command=self.start)
        self.menu.add_command(label="暂停计时", command=self.pause)
        self.menu.add_command(label="重置计时", command=self.reset)
        self.menu.add_separator()
        self.menu.add_command(label="隐藏悬浮窗", command=self._on_close_click)
        self.win.bind("<Button-3>", lambda e: self.menu.post(e.x_root, e.y_root))

    @staticmethod
    def _truncate(text, max_chars):
        return text if len(text) <= max_chars else text[:max_chars - 1] + "…"

    def _on_close_click(self, event=None):
        self.pause()
        self.win.withdraw()
        if self._root and hasattr(self._root, "_on_todo_hidden"):
            self._root._on_todo_hidden(self.todo_id)

    def show(self):
        self.win.deiconify()
        self.win.lift()
        self.win.focus_force()

    def is_visible(self):
        return self.win.state() == "normal"

    def _drag_start(self, event):
        self._dx = event.x_root - self.win.winfo_x()
        self._dy = event.y_root - self.win.winfo_y()

    def _drag_move(self, event):
        self.win.geometry(f"+{event.x_root - self._dx}+{event.y_root - self._dy}")

    # ── 完成状态切换 ──
    def _toggle_complete(self):
        if self.completed:
            self.set_incomplete()
        else:
            self.set_complete()
        self._build_menu()
        if self._root and hasattr(self._root, "_on_todo_state_change"):
            self._root._on_todo_state_change()

    def _toggle_star(self):
        self.starred = not self.starred
        self._build_menu()
        if self._root and hasattr(self._root, "_on_todo_state_change"):
            self._root._on_todo_state_change()

    def set_complete(self):
        self.pause()
        self.completed = True
        self._apply_completed_style()

    def set_incomplete(self):
        self.completed = False
        self._apply_incomplete_style()

    def _apply_completed_style(self):
        self.top.configure(bg=C["green_light"])
        self.title_lbl.configure(bg=C["green_light"], fg=C["green"])
        self.close_lbl.configure(bg=C["green_light"])
        self.done_mark.configure(bg=C["green_light"], text="✓")
        self.time_lbl.configure(fg=C["strikethrough"])
        self._set_btn_enabled(self.btn_start, False)
        self._set_btn_enabled(self.btn_pause, False)
        self._set_btn_enabled(self.btn_reset, False)

    def _apply_incomplete_style(self):
        self.top.configure(bg=C["bg"])
        self.title_lbl.configure(bg=C["bg"], fg=C["text"])
        self.close_lbl.configure(bg=C["bg"])
        self.done_mark.configure(bg=C["bg"], text="")
        self.time_lbl.configure(fg=C["text"])
        self._set_btn_enabled(self.btn_start, True)
        self._set_btn_enabled(self.btn_pause, False)
        self._set_btn_enabled(self.btn_reset, True)

    # ── 计时 ──
    def start(self):
        if self.completed or self.running:
            return
        self.running = True
        self._tick()
        self.top.configure(bg=C["blue_light"])
        self.title_lbl.configure(bg=C["blue_light"], fg=C["blue"])
        self.close_lbl.configure(bg=C["blue_light"])
        self.done_mark.configure(bg=C["blue_light"])
        self._set_btn_enabled(self.btn_start, False)
        self._set_btn_enabled(self.btn_pause, True)
        if self._root and hasattr(self._root, "_on_todo_state_change"):
            self._root._on_todo_state_change()

    def pause(self):
        if not self.running:
            return
        self.running = False
        if self._after_id:
            self.win.after_cancel(self._after_id)
            self._after_id = None
        if not self.completed:
            self.top.configure(bg=C["bg"])
            self.title_lbl.configure(bg=C["bg"], fg=C["text"])
            self.close_lbl.configure(bg=C["bg"])
            self.done_mark.configure(bg=C["bg"])
        else:
            self._apply_completed_style()
        self._set_btn_enabled(self.btn_start, True)
        self._set_btn_enabled(self.btn_pause, False)
        if self._root and hasattr(self._root, "_on_todo_state_change"):
            self._root._on_todo_state_change()

    def reset(self):
        if self.completed:
            return
        was_running = self.running
        self.pause()
        self.elapsed = 0
        self.time_lbl.configure(text=fmt_time(0))
        if was_running:
            self.start()

    def _tick(self):
        if not self.running:
            return
        self.elapsed += 1
        self.time_lbl.configure(text=fmt_time(self.elapsed))
        self._after_id = self.win.after(1000, self._tick)

    @staticmethod
    def _set_btn_enabled(btn_frame, enabled):
        lbl = btn_frame.winfo_children()[0]
        lbl.configure(fg="#FFFFFF" if enabled else "#CCCCCC")

    def destroy(self):
        self.pause()
        self.win.destroy()

    def set_text(self, new_text):
        self.text = new_text
        self.title_lbl.configure(text=self._truncate(new_text, 16))

    def to_dict(self):
        try:
            x = self.win.winfo_x()
            y = self.win.winfo_y()
        except Exception:
            x, y = 100, 100
        return {
            "id": self.todo_id,
            "text": self.text,
            "elapsed": self.elapsed,
            "completed": self.completed,
            "x": x,
            "y": y,
            "visible": self.is_visible(),
            "order": self._order,
            "starred": self.starred,
        }
