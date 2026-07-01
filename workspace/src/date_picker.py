import tkinter as tk
import customtkinter as ctk
from datetime import datetime, timedelta
from .constants import C, FONT, RADIUS
from .utils import flat_btn, fmt_date_cn


class DatePickerDialog:
    """日期选择对话框"""

    def __init__(self, parent, current_date, callback):
        self.callback = callback
        self.date = current_date

        self.dlg = ctk.CTkToplevel(parent, fg_color=C["surface"])
        self.dlg.title("选择日期")
        self.dlg.geometry("340x130")
        self.dlg.resizable(False, False)
        self.dlg.transient(parent)
        self.dlg.grab_set()

        nav = ctk.CTkFrame(self.dlg, fg_color="transparent")
        nav.pack(pady=(16, 10))

        self.prev_btn = ctk.CTkLabel(nav, text="‹", fg_color="transparent",
                                     text_color=C["blue"],
                                     font=(FONT, 22, "bold"), cursor="hand2")
        self.prev_btn.pack(side=tk.LEFT, padx=(0, 14))
        self.prev_btn.bind("<Button-1>", lambda e: self._shift(-1))

        self.date_lbl = ctk.CTkLabel(nav, text=fmt_date_cn(self.date),
                                     fg_color="transparent", text_color=C["text"],
                                     font=(FONT, 13, "bold"), width=20)
        self.date_lbl.pack(side=tk.LEFT)

        self.next_btn = ctk.CTkLabel(nav, text="›", fg_color="transparent",
                                     text_color=C["blue"],
                                     font=(FONT, 22, "bold"), cursor="hand2")
        self.next_btn.pack(side=tk.LEFT, padx=(14, 0))
        self.next_btn.bind("<Button-1>", lambda e: self._shift(1))

        btns = ctk.CTkFrame(self.dlg, fg_color="transparent")
        btns.pack(pady=(4, 0))
        flat_btn(btns, "确定", self._confirm, C["blue"], C["blue_hover"],
                 font_size=10, pady=5, padx=24).pack(side=tk.LEFT, padx=6)
        flat_btn(btns, "取消", self.dlg.destroy, C["text_sec"], C["text_ter"],
                 font_size=10, pady=5, padx=24).pack(side=tk.LEFT, padx=6)

        self.dlg.wait_window()

    def _shift(self, days):
        d = datetime.strptime(self.date, "%Y-%m-%d") + timedelta(days=days)
        self.date = d.strftime("%Y-%m-%d")
        self.date_lbl.configure(text=fmt_date_cn(self.date))

    def _confirm(self):
        self.callback(self.date)
        self.dlg.destroy()
