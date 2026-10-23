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


class TodoCard(ctk.CTkFrame):
    """列表中的待办卡片 — 替代 Listbox 纯文本行。

    只有外壳用 CTk（圆角 + 选中/悬停底色），内部标签与容器全部是纯 tk 控件：
    一张卡片映射/重排的原生开销与 CTk 控件数量成正比（实测 14 个 CTk 控件时
    98 张全量重排 ≈4s），纯 tk 子控件几乎零成本。纯 tk 子控件的 bg 不透明，
    需随卡片状态（默认/选中/悬停）同步 —— 统一由 _apply_fg 完成。
    """

    def __init__(self, parent, todo_widget, seq=0, show_date=False, depth=0,
                 expandable=False, expanded=True, dim=False, progress="", **kw):
        super().__init__(parent, fg_color=C["surface"], corner_radius=RADIUS["card"],
                         cursor="hand2", **kw)
        self.todo_widget = todo_widget
        w = todo_widget
        self._seq = seq
        self._depth = depth
        self._indent = depth * 18    # 期望缩进，pack 后由 _apply_indent 落地
        self._applied_indent = None
        self._expandable = expandable
        self._expanded = expanded
        self._dim = dim
        self._progress = progress

        # ── 左侧色条 ──
        self.accent = tk.Frame(self, bg=self._accent_color(w), width=3)
        self.accent.pack(side=tk.LEFT, fill=tk.Y)
        self.accent.pack_propagate(False)

        # ── 内容区 ──
        content = tk.Frame(self, bg=C["surface"])
        content.pack(side=tk.LEFT, fill=tk.BOTH, expand=True,
                     padx=(12, 10), pady=(6, 6))
        self._plain = [content]

        def plabel(parent, text, fg, font, **kw2):
            lbl = tk.Label(parent, text=text, bg=C["surface"], fg=fg,
                           font=font, cursor="hand2", **kw2)
            self._plain.append(lbl)
            return lbl

        # 左侧图标组
        icons = tk.Frame(content, bg=C["surface"])
        icons.pack(side=tk.LEFT)
        self._plain.append(icons)

        # ▸/▾ 折叠箭头（无子任务时零宽；点击只切换折叠、不选中卡片）
        exp_text = ("▾" if expanded else "▸") if expandable else ""
        self.exp_lbl = plabel(icons, exp_text, C["text_ter"], (FONT, 10))
        self.exp_lbl.pack(side=tk.LEFT)

        # ① 序号
        seq_text = _circled(seq) if seq > 0 else ""
        self.seq_lbl = plabel(icons, seq_text, C["text_sec"], (FONT, 12))
        self.seq_lbl.pack(side=tk.LEFT)

        # ★ 重点标记
        star_text = "★" if w.starred else ""
        star_fg = C["orange"] if w.starred else C["text_ter"]
        self.star_lbl = plabel(icons, star_text, star_fg, (FONT, 12))
        self.star_lbl.pack(side=tk.LEFT)

        # n/m 子任务进度（有子任务时；完成态整枝变绿，进度一眼可见）
        self.prog_lbl = plabel(icons, progress, C["text_ter"], (FONT, 10))
        self.prog_lbl.pack(side=tk.LEFT)

        # 间距
        spacer = tk.Frame(content, bg=C["surface"], width=8)
        spacer.pack(side=tk.LEFT)
        self._plain.append(spacer)

        # 文字（可伸展）
        is_hidden = not w.is_visible()
        self.text_lbl = plabel(content, w.text, self._text_fg(), (FONT, 13), anchor="w")
        self.text_lbl.pack(side=tk.LEFT, fill=tk.X, expand=True)

        # 长文字悬浮提示
        ToolTip(self.text_lbl, lambda: w.text)

        # ── 右侧信息 ──
        # 所有标签始终创建、按状态切换文字（空文字≈0宽），便于跨视图复用卡片。
        # pack 顺序 side=RIGHT（先 pack=最右）：hidden, date, time, done, rem
        right = tk.Frame(content, bg=C["surface"])
        right.pack(side=tk.RIGHT)
        self._plain.append(right)

        w_date = getattr(w, "_date", "") if hasattr(w, "_date") else ""
        self._show_date = bool(show_date and w_date)

        time_fg = C["strikethrough"] if w.completed else (
            C["text_ter"] if is_hidden else C["text"])

        self.hidden_lbl = plabel(right, "◌" if is_hidden else "",
                                 C["text_ter"], (FONT, 12))
        self.hidden_lbl.pack(side=tk.RIGHT, padx=(4, 0))

        self.date_lbl = plabel(
            right, (f"[{fmt_date_short(w_date)}]" if self._show_date else ""),
            C["text_ter"], (FONT, 11))
        self.date_lbl.pack(side=tk.RIGHT, padx=(0, 6))

        self._time_txt = fmt_time(w.elapsed)
        self.time_lbl = plabel(right, self._time_txt, time_fg,
                               ("Consolas", 13), anchor="e")
        self.time_lbl.pack(side=tk.RIGHT, padx=(0, 4))

        self.done_lbl = plabel(right, "[已完成]" if w.completed else "",
                               C["green"], (FONT, 11))
        self.done_lbl.pack(side=tk.RIGHT, padx=(0, 4))

        self.rem_lbl = plabel(right, "", C["orange"], (FONT, 11))
        self.rem_lbl.pack(side=tk.RIGHT, padx=(0, 4))
        self._rem_txt = ""

        # ── 点击 / 悬停 / 拖拽 ──
        self._selected = False
        self._dragging = False
        self._drop_target = False
        self._fg = C["surface"]     # 当前底色（CTk 同值 configure 也会重绘，需守卫）
        self._rv = None             # 已渲染的数据版本 (widget, _version, starred, visible)
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

        # 构造即最终态：标签已按当前数据内联初始化，标记版本并补齐 ⏰ 标签，
        # 使后续 refresh() 在数据未变时可被版本守卫整体跳过
        self._rv = (w, w._version, w.starred, w.is_visible())
        self.update_reminder()
        # 折叠箭头点击独立于「选中卡片」——替换子控件统一绑定的 _on_click。
        # Double 必须一并接管：Tk 对 500ms 内的第二次 press 只发最特异的
        # <Double-Button-1>，否则快速连点箭头会误触「双击加入计时面板」。
        self.exp_lbl.bind("<Button-1>", self._on_exp_click)
        self.exp_lbl.bind("<Double-Button-1>", self._on_exp_click)

    @staticmethod
    def _all_children(widget):
        """递归获取所有子控件"""
        result = []
        for child in widget.winfo_children():
            result.append(child)
            result.extend(TodoCard._all_children(child))
        return result

    @staticmethod
    def _accent_color(w):
        if w.completed:
            return C["green"]
        if w.starred:
            return C["orange"]
        if w.running:
            return C["blue"]
        return C["border"]

    def _base_fg(self):
        """静态底色：选中(蓝) > 完成(绿) > 白。悬停只属于未完成卡。"""
        if self._selected:
            return C["blue_light"]
        if self.todo_widget.completed:
            return C["green_light"]
        return C["surface"]

    def _text_fg(self):
        w = self.todo_widget
        if self._dim or not w.is_visible() or w.completed:
            return C["text_ter"]
        return C["text"]

    def set_selected(self, selected):
        if self._selected == selected:
            return
        self._selected = selected
        self._apply_fg(self._base_fg())

    def set_indent(self, px):
        """树缩进：卡片整体左移（pack padx 左边距）。pack 前只记录，
        pack 后由后续 set_indent / _sync_card 再落地。"""
        self._indent = px
        self._apply_indent()

    def _apply_indent(self):
        if self._indent == self._applied_indent:
            return
        try:
            self.pack_configure(padx=(self._indent, 0))
            self._applied_indent = self._indent
        except tk.TclError:
            pass

    def set_expand(self, expandable, expanded):
        """折叠箭头状态（有子任务才显示 ▸/▾）"""
        if self._expandable == expandable and self._expanded == expanded:
            return
        self._expandable, self._expanded = expandable, expanded
        self.exp_lbl.configure(
            text=("▾" if expanded else "▸") if expandable else "")

    def set_progress(self, text):
        """n/m 子任务进度（无子任务为空串）"""
        if self._progress == text:
            return
        self._progress = text
        self.prog_lbl.configure(text=text)

    def set_dim(self, dim):
        """谓词未命中但作为祖先链保留时弱显（灰字）"""
        if self._dim == dim:
            return
        self._dim = dim
        self.text_lbl.configure(fg=self._text_fg())

    def set_drop_target(self, on):
        """拖拽「成为子级」的目标高亮（绿色边框）"""
        if self._drop_target == on:
            return
        self._drop_target = on
        if on:
            self.configure(border_width=2, border_color=C["green"])
        else:
            self.configure(border_width=0, border_color=C["surface"])

    def _apply_fg(self, color):
        """设置卡片底色，同值跳过（CTk configure 同值也触发整卡重绘）。
        纯 tk 子控件不透明，底色需一并同步。"""
        if self._fg == color:
            return
        self._fg = color
        self.configure(fg_color=color)
        for w in getattr(self, "_plain", []):
            try:
                w.configure(bg=color)
            except tk.TclError:
                pass

    def set_dragging(self, dragging):
        """拖拽中的视觉反馈（外框高亮，不打扰内部配色）"""
        if self._dragging == dragging:
            return
        self._dragging = dragging
        if dragging:
            self.configure(border_width=2, border_color=C["blue"])
        else:
            self.configure(border_width=0, border_color=C["surface"])

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

    def _on_double_click(self, event):
        if hasattr(self, "callback_double"):
            self.callback_double(self.todo_widget.todo_id)

    def _on_right_click(self, event):
        if hasattr(self, "callback_right"):
            self.callback_right(self.todo_widget.todo_id, event)

    def _on_exp_click(self, event):
        """折叠/展开：只切换，不选中卡片"""
        if hasattr(self, "callback_toggle_expand"):
            self.callback_toggle_expand(self.todo_widget.todo_id)
        return "break"

    def _on_enter(self, event):
        if (event.widget is not self or self._selected or self._drag_active
                or self.todo_widget.completed):
            return
        self._apply_fg(C["bg"])

    def _on_leave(self, event):
        if event.widget is not self or self._selected or self._drag_active:
            return
        self._apply_fg(self._base_fg())

    def set_sequence(self, seq):
        if self._seq == seq:
            return
        self._seq = seq
        self.seq_lbl.configure(text=_circled(seq) if seq > 0 else "")

    def set_show_date(self, show):
        """切换日期标签显隐（跨日项）；跨视图复用卡片时由主窗口调用。"""
        w = self.todo_widget
        w_date = getattr(w, "_date", "") if hasattr(w, "_date") else ""
        txt = f"[{fmt_date_short(w_date)}]" if show and w_date else ""
        if self._show_date == show and self.date_lbl.cget("text") == txt:
            return
        self._show_date = show
        self.date_lbl.configure(text=txt)

    def update_reminder(self):
        """刷新 ⏰ 倒计时标签（由主窗口 tick 调用，每 200ms 一次，需同值守卫）"""
        w = self.todo_widget
        if not w.target_seconds:
            txt = ""
        elif w.completed:
            txt = "⏰"
        elif w.running:
            left = w.target_seconds - w.get_live_elapsed()
            txt = "⏰超时" if left <= 0 else f"⏰{fmt_time(left)}"
        else:
            txt = f"⏰{fmt_time(w.target_seconds)}"
        if txt == self._rem_txt:
            return
        self._rem_txt = txt
        self.rem_lbl.configure(text=txt)

    def set_time_text(self, txt):
        """运行中卡片的时间实时刷新（主窗口 tick 调用），同值跳过"""
        if txt == self._time_txt:
            return
        self._time_txt = txt
        self.time_lbl.configure(text=txt)

    def refresh(self):
        """根据 todo_widget 当前状态刷新显示（跨视图复用时调用）。

        数据版本未变则整体跳过 —— 视图切换会复用大量卡片，全部无差别
        configure 是切换卡顿的根源之一。
        """
        w = self.todo_widget
        # starred/visible 走直接赋值不递增 _version，一并纳入守卫键
        rv = (w, w._version, w.starred, w.is_visible())
        if self._rv == rv:
            return
        self._rv = rv
        self._apply_fg(self._base_fg())
        self.accent.configure(bg=self._accent_color(w))

        self.seq_lbl.configure(text=_circled(self._seq) if self._seq > 0 else "")

        star_text = "★" if w.starred else ""
        star_fg = C["orange"] if w.starred else C["text_ter"]
        self.star_lbl.configure(text=star_text, fg=star_fg)

        is_hidden = not w.is_visible()
        self.text_lbl.configure(text=w.text, fg=self._text_fg())

        time_fg = C["strikethrough"] if w.completed else (
            C["text_ter"] if is_hidden else C["text"])
        self._time_txt = fmt_time(w.elapsed)
        self.time_lbl.configure(text=self._time_txt, fg=time_fg)

        self.hidden_lbl.configure(text="◌" if is_hidden else "")
        self.done_lbl.configure(text="[已完成]" if w.completed else "")
        self.set_show_date(self._show_date)
        self.update_reminder()


class SectionHeader(tk.Frame):
    """列表中的区块标题 — 不可点击。

    纯 tk 控件：坐在恒定底色 C["bg"] 上，文字渲染与 CTk 无差异，
    但重挂/重建成本近乎为零（每次刷新都要重排的装饰必须轻）。
    """

    def __init__(self, parent, text, compact=False, **kw):
        bg = C["bg"]
        super().__init__(parent, bg=bg, **kw)
        if compact:
            tk.Label(self, text=text, bg=bg, fg=C["text_ter"],
                     font=(FONT, 9), anchor="w").pack(fill=tk.X, padx=26)
            tk.Frame(self, bg=bg, height=1).pack(fill=tk.X)
        else:
            tk.Frame(self, bg=bg, height=4).pack(fill=tk.X)
            tk.Label(self, text=text, bg=bg, fg=C["text_sec"],
                     font=(FONT, 10, "bold"), anchor="w").pack(fill=tk.X, padx=18)
            tk.Frame(self, bg=bg, height=2).pack(fill=tk.X)


class EmptyHint(tk.Frame):
    """空列表提示（纯 tk，理由同 SectionHeader）"""

    def __init__(self, parent, text="暂无待办", **kw):
        bg = C["bg"]
        super().__init__(parent, bg=bg, **kw)
        tk.Frame(self, bg=bg, height=12).pack(fill=tk.X)
        tk.Label(self, text=text, bg=bg, fg=C["text_ter"],
                 font=(FONT, 10)).pack()
        tk.Frame(self, bg=bg, height=12).pack(fill=tk.X)


class LoadMoreHint(tk.Frame):
    """分页「加载更多」按钮（纯 tk，样式同 EmptyHint）"""

    def __init__(self, parent, text, command, **kw):
        bg = C["bg"]
        super().__init__(parent, bg=bg, **kw)
        tk.Frame(self, bg=bg, height=10).pack(fill=tk.X)
        btn = tk.Label(self, text=text, bg=C["surface"], fg=C["text_sec"],
                       font=(FONT, 10), cursor="hand2", padx=22, pady=7)
        btn.pack()
        btn.bind("<Button-1>", lambda e: command())
        tk.Frame(self, bg=bg, height=10).pack(fill=tk.X)
