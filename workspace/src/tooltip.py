import tkinter as tk
from .constants import C, FONT


class ToolTip:
    """延迟出现的悬浮提示 — 鼠标停留 500ms 后显示"""

    def __init__(self, widget, text_fn):
        self.widget = widget
        self.text_fn = text_fn
        self.tip = None
        self._after_id = None
        widget.bind("<Enter>", self._schedule)
        widget.bind("<Leave>", self._hide)

    def _schedule(self, e=None):
        if self._after_id:
            self.widget.after_cancel(self._after_id)
        # 只对足够长的文字显示提示
        text = self.text_fn()
        if not text or len(text) <= 16:
            return
        self._after_id = self.widget.after(500, self._show)

    def _show(self):
        self._after_id = None
        if self.tip:
            return
        try:
            if not self.widget.winfo_viewable():
                return
        except Exception:
            return

        text = self.text_fn()
        if not text:
            return

        self.tip = tk.Toplevel(self.widget)
        self.tip.overrideredirect(True)
        self.tip.attributes("-topmost", True)

        tk.Label(
            self.tip, text=text, bg="#1F2329", fg="#FFFFFF",
            font=(FONT, 10), padx=10, pady=6, wraplength=360,
            justify="left",
        ).pack()

        # 放在控件下方
        self.tip.update_idletasks()
        x = self.widget.winfo_rootx()
        y = self.widget.winfo_rooty() + self.widget.winfo_height() + 4
        # 不超出屏幕右侧
        sw = self.widget.winfo_screenwidth()
        tw = self.tip.winfo_reqwidth()
        if x + tw > sw:
            x = sw - tw - 8
        self.tip.geometry(f"+{x}+{y}")
        self.tip.lift()

    def _hide(self, e=None):
        if self._after_id:
            self.widget.after_cancel(self._after_id)
            self._after_id = None
        if self.tip:
            try:
                self.tip.destroy()
            except Exception:
                pass
            self.tip = None
