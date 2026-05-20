import tkinter as tk
from .constants import C, FONT
from .utils import fmt_time, fmt_date_short


class TodoCard(tk.Frame):
    """列表中的待办卡片 — 替代 Listbox 纯文本行"""

    def __init__(self, parent, todo_widget, show_date=False, **kw):
        super().__init__(parent, bg=C["surface"], cursor="hand2",
                         highlightthickness=0, bd=0, **kw)
        self.todo_widget = todo_widget
        w = todo_widget

        # ── 左侧色条 ──
        accent_color = self._accent_color(w)
        self.accent = tk.Frame(self, bg=accent_color, width=3)
        self.accent.pack(side=tk.LEFT, fill=tk.Y)
        self.accent.pack_propagate(False)

        # ── 内容区 ──
        content = tk.Frame(self, bg=C["surface"])
        content.pack(side=tk.LEFT, fill=tk.BOTH, expand=True,
                     padx=(12, 10), pady=(8, 8))

        # 左侧图标组
        icons = tk.Frame(content, bg=C["surface"])
        icons.pack(side=tk.LEFT)

        # ★ 重点标记
        star_text = "★" if w.starred else ""
        star_fg = C["orange"] if w.starred else C["text_ter"]
        self.star_lbl = tk.Label(icons, text=star_text, bg=C["surface"], fg=star_fg,
                                 font=(FONT, 10), width=1)
        self.star_lbl.pack(side=tk.LEFT)

        # ●○✓ 状态图标
        status_text, status_fg = self._status_icon(w)
        self.status_lbl = tk.Label(icons, text=status_text, bg=C["surface"],
                                   fg=status_fg, font=(FONT, 12), width=1)
        self.status_lbl.pack(side=tk.LEFT, padx=(2, 0))

        # 间距
        tk.Frame(content, bg=C["surface"], width=8).pack(side=tk.LEFT)

        # 文字（可伸展）
        is_hidden = not w.is_visible()
        text_fg = C["text_ter"] if is_hidden else C["text"]
        self.text_lbl = tk.Label(content, text=w.text, bg=C["surface"], fg=text_fg,
                                 font=(FONT, 11), anchor="w")
        self.text_lbl.pack(side=tk.LEFT, fill=tk.X, expand=True)

        # ── 右侧信息 ──
        right = tk.Frame(content, bg=C["surface"])
        right.pack(side=tk.RIGHT)

        # 隐藏指示器 ◌
        if is_hidden:
            self.hidden_lbl = tk.Label(right, text="◌", bg=C["surface"],
                                       fg=C["text_ter"], font=(FONT, 10))
            self.hidden_lbl.pack(side=tk.RIGHT, padx=(4, 0))

        # 日期标签 (跨日项)
        if show_date:
            w_date = getattr(w, "_date", "") if hasattr(w, "_date") else ""
            if w_date:
                self.date_lbl = tk.Label(right, text=f"[{fmt_date_short(w_date)}]",
                                         bg=C["surface"], fg=C["text_ter"],
                                         font=(FONT, 9))
                self.date_lbl.pack(side=tk.RIGHT, padx=(0, 6))

        # 时间
        time_fg = C["strikethrough"] if w.completed else (
            C["text_ter"] if is_hidden else C["text"])
        self.time_lbl = tk.Label(right, text=fmt_time(w.elapsed), bg=C["surface"],
                                 fg=time_fg, font=("Consolas", 11))
        self.time_lbl.pack(side=tk.RIGHT, padx=(0, 4))

        # 已完成后缀
        if w.completed:
            self.done_lbl = tk.Label(right, text="[已完成]", bg=C["surface"],
                                     fg=C["green"], font=(FONT, 9))
            self.done_lbl.pack(side=tk.RIGHT, padx=(0, 4))

        # ── 点击 / 悬停 ──
        self._selected = False
        self.bind("<Button-1>", self._on_click)
        self.bind("<Double-Button-1>", self._on_double_click)
        self.bind("<Button-3>", self._on_right_click)
        self.bind("<Enter>", self._on_enter)
        self.bind("<Leave>", self._on_leave)
        # 让子控件也响应
        for child in self._all_children(self):
            child.bind("<Button-1>", self._on_click)
            child.bind("<Double-Button-1>", self._on_double_click)
            child.bind("<Button-3>", self._on_right_click)
            child.bind("<Enter>", self._on_enter)
            child.bind("<Leave>", self._on_leave)

    @staticmethod
    def _all_children(widget):
        """递归获取所有子控件"""
        result = []
        for child in widget.winfo_children():
            result.append(child)
            result.extend(TodoCard._all_children(child))
        return result

    @staticmethod
    def _status_icon(w):
        if w.completed:
            return ("✓", C["green"])
        if w.running:
            return ("●", C["blue"])
        return ("○", C["text_sec"])

    @staticmethod
    def _accent_color(w):
        if w.completed:
            return C["green"]
        if w.starred:
            return C["orange"]
        if w.running:
            return C["blue"]
        return C["border"]

    def set_selected(self, selected):
        self._selected = selected
        if selected:
            self.configure(bg=C["blue_light"])
            for child in self._all_children(self):
                try:
                    if child.cget("bg") == C["surface"]:
                        child.configure(bg=C["blue_light"])
                except Exception:
                    pass
        else:
            self.configure(bg=C["surface"])
            for child in self._all_children(self):
                try:
                    if child.cget("bg") == C["blue_light"]:
                        child.configure(bg=C["surface"])
                except Exception:
                    pass

    def _on_click(self, event):
        if hasattr(self, "callback_click"):
            self.callback_click(self.todo_widget.todo_id)

    def _on_double_click(self, event):
        if hasattr(self, "callback_double"):
            self.callback_double(self.todo_widget.todo_id)

    def _on_right_click(self, event):
        if hasattr(self, "callback_right"):
            self.callback_right(self.todo_widget.todo_id, event)

    def _on_enter(self, event):
        if not self._selected:
            self.configure(bg=C["bg"])
            for child in self._all_children(self):
                try:
                    if child.cget("bg") == C["surface"]:
                        child.configure(bg=C["bg"])
                except Exception:
                    pass

    def _on_leave(self, event):
        if not self._selected:
            self.configure(bg=C["surface"])
            for child in self._all_children(self):
                try:
                    if child.cget("bg") == C["bg"]:
                        child.configure(bg=C["surface"])
                except Exception:
                    pass

    def refresh(self):
        """根据 todo_widget 当前状态刷新显示"""
        w = self.todo_widget
        self.accent.configure(bg=self._accent_color(w))

        star_text = "★" if w.starred else ""
        star_fg = C["orange"] if w.starred else C["text_ter"]
        self.star_lbl.configure(text=star_text, fg=star_fg)

        status_text, status_fg = self._status_icon(w)
        self.status_lbl.configure(text=status_text, fg=status_fg)

        is_hidden = not w.is_visible()
        text_fg = C["text_ter"] if is_hidden else C["text"]
        self.text_lbl.configure(text=w.text, fg=text_fg)

        time_fg = C["strikethrough"] if w.completed else (
            C["text_ter"] if is_hidden else C["text"])
        self.time_lbl.configure(text=fmt_time(w.elapsed), fg=time_fg)


class SectionHeader(tk.Frame):
    """列表中的区块标题 — 不可点击"""

    def __init__(self, parent, text, **kw):
        super().__init__(parent, bg=C["surface"],
                         highlightthickness=0, bd=0, **kw)
        tk.Frame(self, bg=C["surface"], height=4).pack(fill=tk.X)
        lbl = tk.Label(self, text=text, bg=C["surface"], fg=C["text_sec"],
                       font=(FONT, 10, "bold"), anchor="w", padx=18)
        lbl.pack(fill=tk.X)
        tk.Frame(self, bg=C["surface"], height=2).pack(fill=tk.X)


class EmptyHint(tk.Frame):
    """空列表提示"""

    def __init__(self, parent, text="暂无待办", **kw):
        super().__init__(parent, bg=C["surface"],
                         highlightthickness=0, bd=0, **kw)
        tk.Frame(self, bg=C["surface"], height=12).pack(fill=tk.X)
        lbl = tk.Label(self, text=text, bg=C["surface"], fg=C["text_ter"],
                       font=(FONT, 10))
        lbl.pack()
        tk.Frame(self, bg=C["surface"], height=12).pack(fill=tk.X)
