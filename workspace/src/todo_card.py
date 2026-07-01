import tkinter as tk
import customtkinter as ctk
from .constants import C, FONT, RADIUS
from .utils import fmt_time, fmt_date_short
from .tooltip import ToolTip

# 带圈数字 ①–⑳
_CIRCLED = "①②③④⑤⑥⑦⑧⑨⑩⑪⑫⑬⑭⑮⑯⑰⑱⑲⑳"


def _circled(n):
    if 1 <= n <= len(_CIRCLED):
        return _CIRCLED[n - 1]
    return f"({n})"


# CTkLabel 默认渲染高 35px（与字号无关），远大于文字实际高度（~15px），
# 显式压到 22px 让卡片行更紧凑；文字仍宽松容纳、不裁切。
_CARD_LABEL_H = 22


class TodoCard(ctk.CTkFrame):
    """列表中的待办卡片 — 替代 Listbox 纯文本行"""

    def __init__(self, parent, todo_widget, seq=0, show_date=False, **kw):
        super().__init__(parent, fg_color=C["surface"], corner_radius=RADIUS["card"],
                         cursor="hand2", **kw)
        self.todo_widget = todo_widget
        w = todo_widget
        self._seq = seq

        # ── 左侧色条 ──
        accent_color = self._accent_color(w)
        self.accent = ctk.CTkFrame(self, fg_color=accent_color, width=3, height=0)
        self.accent.pack(side=tk.LEFT, fill=tk.Y)
        self.accent.pack_propagate(False)

        # ── 内容区 ──
        content = ctk.CTkFrame(self, fg_color="transparent")
        content.pack(side=tk.LEFT, fill=tk.BOTH, expand=True,
                     padx=(12, 10), pady=(2, 2))

        # 左侧图标组
        icons = ctk.CTkFrame(content, fg_color="transparent")
        icons.pack(side=tk.LEFT)

        # ① 序号
        seq_text = _circled(seq) if seq > 0 else ""
        self.seq_lbl = ctk.CTkLabel(icons, text=seq_text, fg_color="transparent",
                                    text_color=C["text_sec"], font=(FONT, 10),
                                    height=_CARD_LABEL_H)
        self.seq_lbl.pack(side=tk.LEFT)

        # ★ 重点标记
        star_text = "★" if w.starred else ""
        star_fg = C["orange"] if w.starred else C["text_ter"]
        self.star_lbl = ctk.CTkLabel(icons, text=star_text, fg_color="transparent",
                                     text_color=star_fg, font=(FONT, 10),
                                     height=_CARD_LABEL_H)
        self.star_lbl.pack(side=tk.LEFT)

        # ●○✓ 状态图标
        status_text, status_fg = self._status_icon(w)
        self.status_lbl = ctk.CTkLabel(icons, text=status_text, fg_color="transparent",
                                       text_color=status_fg, font=(FONT, 12),
                                       height=_CARD_LABEL_H)
        self.status_lbl.pack(side=tk.LEFT, padx=(2, 0))

        # 间距
        ctk.CTkFrame(content, fg_color="transparent", width=8, height=0).pack(side=tk.LEFT)

        # 文字（可伸展）
        is_hidden = not w.is_visible()
        text_fg = C["text_ter"] if is_hidden else C["text"]
        self.text_lbl = ctk.CTkLabel(content, text=w.text, fg_color="transparent",
                                     text_color=text_fg, font=(FONT, 11), anchor="w",
                                     height=_CARD_LABEL_H)
        self.text_lbl.pack(side=tk.LEFT, fill=tk.X, expand=True)

        # 长文字悬浮提示
        ToolTip(self.text_lbl, lambda: w.text)

        # ── 右侧信息 ──
        right = ctk.CTkFrame(content, fg_color="transparent")
        right.pack(side=tk.RIGHT)

        # 隐藏指示器 ◌
        if is_hidden:
            self.hidden_lbl = ctk.CTkLabel(right, text="◌", fg_color="transparent",
                                           text_color=C["text_ter"], font=(FONT, 10),
                                           height=_CARD_LABEL_H)
            self.hidden_lbl.pack(side=tk.RIGHT, padx=(4, 0))

        # 日期标签 (跨日项)
        if show_date:
            w_date = getattr(w, "_date", "") if hasattr(w, "_date") else ""
            if w_date:
                self.date_lbl = ctk.CTkLabel(right, text=f"[{fmt_date_short(w_date)}]",
                                             fg_color="transparent", text_color=C["text_ter"],
                                             font=(FONT, 9), height=_CARD_LABEL_H)
                self.date_lbl.pack(side=tk.RIGHT, padx=(0, 6))

        # 时间
        time_fg = C["strikethrough"] if w.completed else (
            C["text_ter"] if is_hidden else C["text"])
        self.time_lbl = ctk.CTkLabel(right, text=fmt_time(w.elapsed), fg_color="transparent",
                                     text_color=time_fg, font=("Consolas", 11), anchor="e",
                                     height=_CARD_LABEL_H)
        self.time_lbl.pack(side=tk.RIGHT, padx=(0, 4))

        # 已完成后缀
        if w.completed:
            self.done_lbl = ctk.CTkLabel(right, text="[已完成]", fg_color="transparent",
                                         text_color=C["green"], font=(FONT, 9),
                                         height=_CARD_LABEL_H)
            self.done_lbl.pack(side=tk.RIGHT, padx=(0, 4))

        # 计时提醒标记 ⏰ + 倒计时（仅当设置了目标时长）
        if w.target_seconds:
            self.rem_lbl = ctk.CTkLabel(right, text="⏰", fg_color="transparent",
                                        text_color=C["orange"], font=(FONT, 9),
                                        height=_CARD_LABEL_H)
            self.rem_lbl.pack(side=tk.RIGHT, padx=(0, 4))

        # ── 点击 / 悬停 / 拖拽 ──
        self._selected = False
        self._drag_start = None    # 拖拽起点 (x_root, y_root)
        self._drag_active = False  # 是否已进入拖拽
        self.bind("<Button-1>", self._on_click)
        self.bind("<Double-Button-1>", self._on_double_click)
        self.bind("<Button-3>", self._on_right_click)
        self.bind("<B1-Motion>", self._on_drag_motion)
        self.bind("<ButtonRelease-1>", self._on_drag_release)
        self.bind("<Enter>", self._on_enter)
        self.bind("<Leave>", self._on_leave)
        # 子控件只绑定点击/拖拽，悬停统一由外层 Frame 处理避免闪烁
        for child in self._all_children(self):
            child.bind("<Button-1>", self._on_click)
            child.bind("<Double-Button-1>", self._on_double_click)
            child.bind("<Button-3>", self._on_right_click)
            child.bind("<B1-Motion>", self._on_drag_motion)
            child.bind("<ButtonRelease-1>", self._on_drag_release)

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
        self.configure(fg_color=C["blue_light"] if selected else C["surface"])

    def _on_click(self, event):
        # 记录拖拽起点；移动超过阈值才算拖拽，否则视为普通点击
        self._drag_start = (event.x_root, event.y_root)
        self._drag_active = False
        if hasattr(self, "callback_click"):
            self.callback_click(self.todo_widget.todo_id)

    def _on_drag_motion(self, event):
        if not self._drag_start:
            return
        sx, sy = self._drag_start
        if not self._drag_active:
            # 5px 阈值，区分点击与拖拽
            if abs(event.x_root - sx) < 5 and abs(event.y_root - sy) < 5:
                return
            self._drag_active = True
            if hasattr(self, "callback_drag_start"):
                self.callback_drag_start(self.todo_widget.todo_id)
        if hasattr(self, "callback_drag_move"):
            self.callback_drag_move(self.todo_widget.todo_id, event.y_root)

    def _on_drag_release(self, event):
        if self._drag_active:
            if hasattr(self, "callback_drag_drop"):
                self.callback_drag_drop(self.todo_widget.todo_id, event.y_root)
        self._drag_start = None
        self._drag_active = False

    def set_dragging(self, dragging):
        """拖拽中的视觉反馈（外框高亮，不打扰内部配色）"""
        if dragging:
            self.configure(border_width=2, border_color=C["blue"])
        else:
            self.configure(border_width=0, border_color=C["surface"])

    def update_reminder(self):
        """刷新 ⏰ 倒计时标签（由主窗口 tick 调用）"""
        if not hasattr(self, "rem_lbl"):
            return
        w = self.todo_widget
        if not w.target_seconds:
            return
        if w.completed:
            txt = "⏰"
        elif w.running:
            left = w.target_seconds - w.get_live_elapsed()
            txt = "⏰超时" if left <= 0 else f"⏰{fmt_time(left)}"
        else:
            txt = f"⏰{fmt_time(w.target_seconds)}"
        self.rem_lbl.configure(text=txt)

    def _on_double_click(self, event):
        if hasattr(self, "callback_double"):
            self.callback_double(self.todo_widget.todo_id)

    def _on_right_click(self, event):
        if hasattr(self, "callback_right"):
            self.callback_right(self.todo_widget.todo_id, event)

    def _on_enter(self, event):
        if event.widget is not self or self._selected or self._drag_active:
            return
        self.configure(fg_color=C["bg"])

    def _on_leave(self, event):
        if event.widget is not self or self._selected or self._drag_active:
            return
        self.configure(fg_color=C["surface"])

    def set_sequence(self, seq):
        self._seq = seq
        self.seq_lbl.configure(text=_circled(seq) if seq > 0 else "")

    def refresh(self):
        """根据 todo_widget 当前状态刷新显示"""
        w = self.todo_widget
        self.accent.configure(fg_color=self._accent_color(w))

        self.seq_lbl.configure(text=_circled(self._seq) if self._seq > 0 else "")

        star_text = "★" if w.starred else ""
        star_fg = C["orange"] if w.starred else C["text_ter"]
        self.star_lbl.configure(text=star_text, text_color=star_fg)

        status_text, status_fg = self._status_icon(w)
        self.status_lbl.configure(text=status_text, text_color=status_fg)

        is_hidden = not w.is_visible()
        text_fg = C["text_ter"] if is_hidden else C["text"]
        self.text_lbl.configure(text=w.text, text_color=text_fg)

        time_fg = C["strikethrough"] if w.completed else (
            C["text_ter"] if is_hidden else C["text"])
        self.time_lbl.configure(text=fmt_time(w.elapsed), text_color=time_fg)


class SectionHeader(ctk.CTkFrame):
    """列表中的区块标题 — 不可点击"""

    def __init__(self, parent, text, compact=False, **kw):
        super().__init__(parent, fg_color=C["surface"], corner_radius=0, **kw)
        if compact:
            lbl = ctk.CTkLabel(self, text=text, fg_color="transparent",
                               text_color=C["text_ter"], font=(FONT, 9), anchor="w")
            lbl.pack(fill=tk.X, padx=26)
            ctk.CTkFrame(self, fg_color="transparent", height=1).pack(fill=tk.X)
        else:
            ctk.CTkFrame(self, fg_color="transparent", height=4).pack(fill=tk.X)
            lbl = ctk.CTkLabel(self, text=text, fg_color="transparent",
                               text_color=C["text_sec"], font=(FONT, 10, "bold"), anchor="w")
            lbl.pack(fill=tk.X, padx=18)
            ctk.CTkFrame(self, fg_color="transparent", height=2).pack(fill=tk.X)


class EmptyHint(ctk.CTkFrame):
    """空列表提示"""

    def __init__(self, parent, text="暂无待办", **kw):
        super().__init__(parent, fg_color=C["surface"], corner_radius=0, **kw)
        ctk.CTkFrame(self, fg_color="transparent", height=12).pack(fill=tk.X)
        lbl = ctk.CTkLabel(self, text=text, fg_color="transparent",
                           text_color=C["text_ter"], font=(FONT, 10))
        lbl.pack()
        ctk.CTkFrame(self, fg_color="transparent", height=12).pack(fill=tk.X)
