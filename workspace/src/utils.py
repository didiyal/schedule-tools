import tkinter as tk
from datetime import datetime
from .constants import C, FONT, WEEKDAYS


def flat_btn(parent, text, cmd, bg, hover_bg,
             font_size=10, pady=6, padx=18, fg="#FFFFFF"):
    """创建飞书风格的扁平按钮 (Frame + Label 组合)"""
    f = tk.Frame(parent, bg=bg, cursor="hand2", highlightthickness=0, bd=0)
    lbl = tk.Label(f, text=text, bg=bg, fg=fg, font=(FONT, font_size),
                   cursor="hand2", bd=0, padx=padx, pady=pady)
    lbl.pack()
    for w in (f, lbl):
        w.bind("<Button-1>", lambda e: cmd())
        w.bind("<Enter>", lambda e, ff=f, ll=lbl, hc=hover_bg, hf=fg:
               (ff.configure(bg=hc), ll.configure(bg=hc, fg=hf)))
        w.bind("<Leave>", lambda e, ff=f, ll=lbl, bc=bg, bf=fg:
               (ff.configure(bg=bc), ll.configure(bg=bc, fg=bf)))
    return f


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
