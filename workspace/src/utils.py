import tkinter as tk
import customtkinter as ctk
from datetime import datetime
from .constants import C, FONT, WEEKDAYS, RADIUS


def flat_btn(parent, text, cmd, bg, hover_bg,
             font_size=10, pady=6, padx=18, fg="#FFFFFF"):
    """飞书风扁平按钮 —— 基于 CTkButton（圆角 + hover 平滑过渡）。
    保留原签名，padx 由 CTkButton 内边距自动吸收。"""
    height = font_size + 2 * pady + 6  # 近似原 pady 撑高
    btn = ctk.CTkButton(
        parent, text=text, command=cmd,
        fg_color=bg, hover_color=hover_bg, text_color=fg,
        corner_radius=RADIUS["button"],
        font=(FONT, font_size),
        border_width=0,
        height=height,
    )
    return btn


def get_today_str():
    return datetime.now().strftime("%Y-%m-%d")


def fmt_date_cn(date_str):
    try:
        d = datetime.strptime(date_str, "%Y-%m-%d")
        wd = WEEKDAYS[d.weekday()]
        return f"{d.year}年{d.month}月{d.day}日 {wd}"
    except Exception:
        return date_str


def fmt_date_short(date_str):
    try:
        d = datetime.strptime(date_str, "%Y-%m-%d")
        return f"{d.month}月{d.day}日"
    except Exception:
        return date_str


def fmt_time(s):
    h, m = divmod(s, 3600)
    m, s = divmod(m, 60)
    return f"{h:02d}:{m:02d}:{s:02d}"
