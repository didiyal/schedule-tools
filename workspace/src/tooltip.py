import tkinter as tk
from .constants import C, FONT


class ToolTip:
    """延迟出现的悬浮提示"""

    def __init__(self, widget, text_fn):
        self.widget = widget
        self.text_fn = text_fn
        self.tip = None
        self._after_id = None
        widget.bind("<Enter>", self._schedule)
        widget.bind("<Leave>", self._hide)

    def _schedule(self, e=None):
        self._after_id = self.widget.after(500, self._show)

    def _show(self):
        if self.tip:
            return
        text = self.text_fn()
        self.tip = tk.Toplevel(self.widget)
        self.tip.overrideredirect(True)
        self.tip.attributes("-topmost", True)
        x = self.widget.winfo_rootx()
        y = self.widget.winfo_rooty() + self.widget.winfo_height() + 4
        self.tip.geometry(f"+{x}+{y}")
        tk.Label(
            self.tip, text=text, bg="#1F2329", fg="#FFFFFF",
            font=(FONT, 10), padx=10, pady=6, wraplength=320,
            justify="left",
        ).pack()

    def _hide(self, e=None):
        if self._after_id:
            self.widget.after_cancel(self._after_id)
            self._after_id = None
        if self.tip:
            self.tip.destroy()
            self.tip = None
