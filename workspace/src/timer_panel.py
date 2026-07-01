import tkinter as tk
import customtkinter as ctk
from collections.abc import Callable
from .constants import C, FONT, RADIUS
from .utils import fmt_time
from .tooltip import ToolTip


class TimerPanel:
    """统一计时悬浮面板 —— 替代多个独立 TodoWidget 窗口"""

    DOCK_TAB = 8          # 收起后露出的标签高度
    DOCK_THRESHOLD = 60   # 拖到距屏幕顶部多少 px 触发吸附
    ANIM_STEP = 30        # 动画每帧移动像素
    ANIM_INTERVAL = 10    # 动画帧间隔 ms
    COLLAPSE_DELAY = 500  # 鼠标离开后延迟收起 ms

    def __init__(self, master_root, on_state_change=None):
        self._root = master_root
        self.on_state_change = on_state_change
        self._rows: dict[str, "TimerRow"] = {}
        self._row_callbacks: dict[str, Callable] = {}  # todo_id → callback ref for removal
        self._tick_after_id = None

        # 吸附状态
        self._docked = False        # 是否已吸附顶部
        self._collapsed = False     # 是否已收起
        self._animating = False     # 是否正在动画中
        self._dragging = False      # 是否正在拖拽中
        self._collapse_after_id = None
        self._anim_after_id = None
        self._panel_h = 480
        self._expanded_y = 0        # 展开时的 y 坐标

        # ── Toplevel ──
        self.win = tk.Toplevel(master_root)
        self.win.overrideredirect(True)
        self.win.attributes("-topmost", True)
        self.win.geometry("420x480")
        self.win.withdraw()

        # 鼠标进出追踪（用于展开/收起）
        self.win.bind("<Enter>", self._on_panel_enter)
        self.win.bind("<Leave>", self._on_panel_leave)

        shadow = ctk.CTkFrame(self.win, fg_color=C["shadow"])
        shadow.pack(fill=tk.BOTH, expand=True)

        card = ctk.CTkFrame(shadow, fg_color=C["surface"], corner_radius=RADIUS["card"])
        card.pack(fill=tk.BOTH, expand=True, padx=2, pady=2)

        # ── 标题栏 ──
        self.title_bar = ctk.CTkFrame(card, fg_color=C["bg"], height=32)
        self.title_bar.pack(fill=tk.X, side=tk.TOP)
        self.title_bar.pack_propagate(False)

        ctk.CTkLabel(self.title_bar, text="⏱ 计时面板", fg_color="transparent",
                     text_color=C["text"], font=(FONT, 11, "bold")
                     ).pack(side=tk.LEFT, padx=12, pady=4)

        close_lbl = ctk.CTkLabel(self.title_bar, text="✕", fg_color="transparent",
                                 text_color=C["text_ter"], font=(FONT, 12),
                                 cursor="hand2", padx=8)
        close_lbl.pack(side=tk.RIGHT, pady=4)
        close_lbl.bind("<Button-1>", lambda e: self.hide())
        close_lbl.bind("<Enter>",
                       lambda e, c=close_lbl: c.configure(text_color=C["text"], fg_color=C["border"]))
        close_lbl.bind("<Leave>",
                       lambda e, c=close_lbl: c.configure(text_color=C["text_ter"], fg_color=C["bg"]))

        # 拖拽
        for widget in (self.title_bar,):
            widget.bind("<Button-1>", self._drag_start)
            widget.bind("<B1-Motion>", self._drag_move)
            widget.bind("<ButtonRelease-1>", self._on_drag_release)

        # 分隔线
        ctk.CTkFrame(card, fg_color=C["border"], height=1).pack(fill=tk.X)

        # ── 可滚动内容区 ──
        self.canvas = tk.Canvas(card, bg=C["surface"], highlightthickness=0, bd=0)
        self.scrollbar = tk.Scrollbar(card, orient=tk.VERTICAL,
                                      command=self.canvas.yview,
                                      bg=C["bg"], troughcolor=C["bg"],
                                      activebackground=C["blue"])
        self.canvas.configure(yscrollcommand=self.scrollbar.set)

        self.inner_frame = tk.Frame(self.canvas, bg=C["surface"])
        self.inner_id = self.canvas.create_window((0, 0), window=self.inner_frame,
                                                  anchor="nw")

        self.canvas.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        self.scrollbar.pack(side=tk.RIGHT, fill=tk.Y)

        self.canvas.bind("<Configure>", self._on_canvas_configure)
        self.inner_frame.bind("<Configure>",
                              lambda e: self.canvas.configure(
                                  scrollregion=self.canvas.bbox("all")))

        # 鼠标滚轮 — 绑定到窗口级(bind_all)：停在面板内行(子控件)上时事件发给
        # 子控件而非 Canvas，Canvas 上的绑定不会触发。改用 bind_all 后任意位置
        # 都能滚；_on_mousewheel 内按所属顶层窗口做坐标守卫，只滚本面板不串扰主窗口
        self.win.bind_all("<MouseWheel>", self._on_mousewheel, add="+")
        self.win.bind_all("<Button-4>", self._on_mousewheel, add="+")
        self.win.bind_all("<Button-5>", self._on_mousewheel, add="+")

        # 空面板提示
        self._empty_hint = ctk.CTkLabel(self.inner_frame,
                                        text="暂无计时项\n双击列表中的待办加入",
                                        fg_color="transparent", text_color=C["text_ter"],
                                        font=(FONT, 10), justify="center")
        self._empty_hint.pack(fill=tk.X, pady=40)

    # ── 拖拽 + 吸附 ──
    def _drag_start(self, event):
        self._cancel_collapse()
        if self._collapsed:
            # 从收起状态拖拽 → 先展开再拖
            self._expand(animate=False)
        self._dx = event.x_root - self.win.winfo_x()
        self._dy = event.y_root - self.win.winfo_y()
        self._dragging = True

    def _drag_move(self, event):
        new_x = event.x_root - self._dx
        new_y = event.y_root - self._dy
        self.win.geometry(f"+{new_x}+{new_y}")
        # 拖离顶部超过阈值 → 解除吸附
        if self._docked and new_y > self.DOCK_THRESHOLD + 20:
            self._exit_dock()

    def _on_drag_release(self, event):
        self._dragging = False
        if self._docked:
            return
        y = self.win.winfo_y()
        if y <= self.DOCK_THRESHOLD:
            self._enter_dock()

    # ── 吸附状态 ──

    def _enter_dock(self):
        self._docked = True
        self._expanded_y = 0  # 展开时贴屏幕顶部
        # 记下面板实际高度
        try:
            h = self.win.winfo_height()
            if h > 50:
                self._panel_h = h
        except Exception:
            pass
        self._collapsed_y = -(self._panel_h - self.DOCK_TAB)
        self._animate_to(self._collapsed_y, self._finish_collapse)

    def _exit_dock(self):
        self._cancel_collapse()
        self._cancel_anim()
        self._docked = False
        self._collapsed = False

    def _expand(self, animate=True):
        """展开面板"""
        self._cancel_collapse()
        self._cancel_anim()
        if animate and self._collapsed:
            self._animate_to(self._expanded_y, self._finish_expand)
        else:
            self.win.geometry(f"+{self.win.winfo_x()}+{self._expanded_y}")
            self._finish_expand()

    def _collapse(self):
        """收起面板（仅露出标签条）"""
        if self._animating:
            return
        self._cancel_anim()
        self._animate_to(self._collapsed_y, self._finish_collapse)

    def _finish_expand(self):
        self._collapsed = False
        self._animating = False

    def _finish_collapse(self):
        self._collapsed = True
        self._animating = False

    def _animate_to(self, target_y, on_done):
        """平滑移动到目标 Y 坐标"""
        self._animating = True
        cur_y = self.win.winfo_y()
        if cur_y == target_y:
            on_done()
            return
        step = self.ANIM_STEP if target_y > cur_y else -self.ANIM_STEP
        next_y = cur_y + step
        if (step > 0 and next_y >= target_y) or (step < 0 and next_y <= target_y):
            next_y = target_y
        self.win.geometry(f"+{self.win.winfo_x()}+{next_y}")
        if next_y == target_y:
            on_done()
        else:
            self._anim_after_id = self.win.after(
                self.ANIM_INTERVAL, lambda: self._animate_to(target_y, on_done))

    def _cancel_anim(self):
        if self._anim_after_id:
            self.win.after_cancel(self._anim_after_id)
            self._anim_after_id = None
        self._animating = False

    def _cancel_collapse(self):
        if self._collapse_after_id:
            self.win.after_cancel(self._collapse_after_id)
            self._collapse_after_id = None

    # ── 鼠标进出 ──

    def _on_panel_enter(self, event):
        self._cancel_collapse()
        if self._docked and self._collapsed:
            self._expand()

    def _on_panel_leave(self, event):
        if not self._docked or self._collapsed:
            return
        # 子控件切换也会触发 Leave → 延迟检查鼠标是否真的离开了面板
        self._cancel_collapse()
        self._collapse_after_id = self.win.after(100, self._check_leave)

    def _check_leave(self):
        """延迟检查：鼠标真的离开了面板区域才触发收起"""
        self._collapse_after_id = None
        try:
            x = self.win.winfo_pointerx()
            y = self.win.winfo_pointery()
            wx = self.win.winfo_rootx()
            wy = self.win.winfo_rooty()
            ww = self.win.winfo_width()
            wh = self.win.winfo_height()
            inside = wx <= x < wx + ww and wy <= y < wy + wh
        except Exception:
            inside = True
        if inside:
            return
        if self._docked and not self._collapsed and not self._dragging:
            self._collapse_after_id = self.win.after(
                self.COLLAPSE_DELAY, self._collapse)

    # ── Canvas ──
    def _on_canvas_configure(self, event):
        self.canvas.itemconfig(self.inner_id, width=event.width)

    def _on_mousewheel(self, event):
        # 仅当光标位于本面板内时滚动，不串扰主窗口
        w = self.win.winfo_containing(event.x_root, event.y_root)
        try:
            if w is None or w.winfo_toplevel() is not self.win:
                return
        except Exception:
            return
        if event.num == 4:
            delta = -1
        elif event.num == 5:
            delta = 1
        else:
            delta = event.delta / 120
        self.canvas.yview_scroll(int(-delta), "units")

    # ── 待办管理 ──

    def add_todo(self, w):
        """将 TodoWidget 加入面板"""
        if w.todo_id in self._rows:
            return
        w._in_panel = True
        cb = lambda: self._on_row_change(w.todo_id)
        w.add_callback(cb)
        self._row_callbacks[w.todo_id] = cb

        self._empty_hint.pack_forget()

        row = TimerRow(self.inner_frame, w, self)
        row.pack(fill=tk.X, padx=2, pady=(0, 2))
        self._rows[w.todo_id] = row

        self._ensure_ticking()
        self.show()

    def remove_todo(self, todo_id):
        """从面板移除待办"""
        if todo_id not in self._rows:
            return
        row = self._rows.pop(todo_id)
        w = row.todo_widget
        w._in_panel = False
        cb = self._row_callbacks.pop(todo_id, None)
        if cb:
            w.remove_callback(cb)
        w.pause()
        row.destroy()

        if not self._rows:
            self._stop_ticking()
            self._empty_hint.pack(fill=tk.X, pady=40)
            self.hide()

        if self.on_state_change:
            self.on_state_change()

    def has_todo(self, todo_id):
        return todo_id in self._rows

    def refresh_all(self):
        """刷新所有行的显示"""
        for row in self._rows.values():
            row.refresh()

    def _on_row_change(self, todo_id):
        """TodoWidget 状态变更回调"""
        if todo_id in self._rows:
            self._rows[todo_id].refresh()
        self._ensure_ticking()
        if self.on_state_change:
            self.on_state_change()

    def _on_row_action(self, todo_id):
        """行按钮点击回调"""
        if todo_id in self._rows:
            self._rows[todo_id].refresh()
        self._ensure_ticking()
        if self.on_state_change:
            self.on_state_change()

    # ── 全局 Tick ──

    def _ensure_ticking(self):
        if any(row.todo_widget.running for row in self._rows.values()):
            if not self._tick_after_id:
                self._tick()

    def _tick(self):
        self._tick_after_id = None
        for row in list(self._rows.values()):
            row.refresh()
        if any(row.todo_widget.running for row in self._rows.values()):
            self._tick_after_id = self.win.after(200, self._tick)

    def _stop_ticking(self):
        if self._tick_after_id:
            self.win.after_cancel(self._tick_after_id)
            self._tick_after_id = None

    # ── 显示 / 隐藏 ──

    def show(self):
        # 始终先 deiconify：面板可能被 hide() 隐藏（withdraw），
        # 此时仅动画 y 坐标不会让它重新出现
        self.win.deiconify()
        if self._docked and self._collapsed:
            self._expand()
        else:
            self.win.lift()
            self.win.focus_force()

    def hide(self):
        self._cancel_collapse()
        self._cancel_anim()
        self.win.withdraw()

    def is_visible(self):
        return self.win.state() == "normal"

    # ── 批量操作 ──

    def start_all(self):
        for row in self._rows.values():
            row.todo_widget.start()
        self.refresh_all()
        self._ensure_ticking()

    def pause_all(self):
        for row in self._rows.values():
            row.todo_widget.pause()
        self.refresh_all()

    # ── 位置持久化 ──

    def get_position(self):
        try:
            if self._docked:
                return (self.win.winfo_x(), self._expanded_y)
            return (self.win.winfo_x(), self.win.winfo_y())
        except Exception:
            return (100, 100)

    def set_position(self, x, y):
        try:
            self.win.geometry(f"+{x}+{y}")
            # 检测保存的位置是否在顶部 → 恢复吸附
            if y <= self.DOCK_THRESHOLD:
                self._enter_dock()
        except Exception:
            pass


class TimerRow(ctk.CTkFrame):
    """面板中的一行 —— 显示一个待办的计时信息和控制按钮"""

    def __init__(self, parent, todo_widget, panel: TimerPanel, **kw):
        super().__init__(parent, fg_color=C["surface"], corner_radius=RADIUS["card"],
                         cursor="hand2", **kw)
        self.todo_widget = todo_widget
        self.panel = panel
        w = todo_widget

        # ── 行间分隔线 ──
        sep = ctk.CTkFrame(self, fg_color=C["border"], height=1)
        sep.pack(fill=tk.X, side=tk.TOP)

        # ── 左侧色条 ──
        self.accent = ctk.CTkFrame(self, fg_color=self._accent_color(), width=3, height=0)
        self.accent.pack(side=tk.LEFT, fill=tk.Y)
        self.accent.pack_propagate(False)

        # ── 内容区 ──
        content = ctk.CTkFrame(self, fg_color="transparent")
        content.pack(side=tk.LEFT, fill=tk.BOTH, expand=True,
                     padx=(10, 8), pady=(5, 5))

        # ── 第一行：状态 + 重点 + 文字 ──
        top_row = ctk.CTkFrame(content, fg_color="transparent")
        top_row.pack(fill=tk.X)

        status_text, status_fg = self._status_icon()
        self.status_lbl = ctk.CTkLabel(top_row, text=status_text, fg_color="transparent",
                                       text_color=status_fg, font=(FONT, 11))
        self.status_lbl.pack(side=tk.LEFT)

        star_text = "★" if w.starred else ""
        star_fg = C["orange"] if w.starred else C["text_ter"]
        self.star_lbl = ctk.CTkLabel(top_row, text=star_text, fg_color="transparent",
                                     text_color=star_fg, font=(FONT, 10))
        self.star_lbl.pack(side=tk.LEFT, padx=(1, 6))

        self.text_lbl = ctk.CTkLabel(top_row, text=self._truncate(w.text, 30),
                                     fg_color="transparent", text_color=C["text"],
                                     font=(FONT, 10, "bold"), anchor="w")
        self.text_lbl.pack(side=tk.LEFT, fill=tk.X, expand=True)
        ToolTip(self.text_lbl, lambda: w.text)

        # ── 第二行：时间 + 按钮 ──
        mid_row = ctk.CTkFrame(content, fg_color="transparent")
        mid_row.pack(fill=tk.X, pady=(3, 0))

        self.time_lbl = ctk.CTkLabel(mid_row, text=fmt_time(w.get_live_elapsed()),
                                     fg_color="transparent", text_color=self._time_color(),
                                     font=("Consolas", 18, "bold"))
        self.time_lbl.pack(side=tk.LEFT)

        ctk.CTkFrame(mid_row, fg_color="transparent", height=0).pack(side=tk.LEFT, fill=tk.X, expand=True)

        self.btn_start = self._mini_btn(mid_row, "▶", self._on_start)
        self.btn_start.pack(side=tk.RIGHT, padx=1)

        self.btn_pause = self._mini_btn(mid_row, "⏸", self._on_pause)
        self.btn_pause.pack(side=tk.RIGHT, padx=1)

        self.btn_reset = self._mini_btn(mid_row, "↺", self._on_reset)
        self.btn_reset.pack(side=tk.RIGHT, padx=1)

        self.btn_remove = self._mini_btn(mid_row, "✕", self._on_remove,
                                         fg=C["text_ter"], hover_bg=C["red_light"])
        self.btn_remove.pack(side=tk.RIGHT, padx=1)

        # ── 右键菜单 ──
        self._build_menu()
        for child in self._all_children(self):
            child.bind("<Button-3>", lambda e, s=self: s.menu.post(e.x_root, e.y_root))

        self.refresh()

    # ── 迷你按钮 ──
    @staticmethod
    def _mini_btn(parent, text, cmd, fg=C["text_sec"], hover_bg=C["blue_light"]):
        btn = ctk.CTkButton(parent, text=text, command=cmd,
                            width=28, height=24,
                            fg_color=C["surface"], hover_color=hover_bg,
                            text_color=fg, corner_radius=RADIUS["button"],
                            font=(FONT, 10), border_width=0)
        return btn

    @staticmethod
    def _truncate(text, max_chars):
        return text if len(text) <= max_chars else text[:max_chars - 1] + "…"

    @staticmethod
    def _all_children(widget):
        result = []
        for child in widget.winfo_children():
            result.append(child)
            result.extend(TimerRow._all_children(child))
        return result

    # ── 状态图标/颜色 ──
    def _status_icon(self):
        w = self.todo_widget
        if w.completed:
            return ("✓", C["green"])
        if w.running:
            return ("●", C["blue"])
        return ("○", C["text_sec"])

    def _accent_color(self):
        w = self.todo_widget
        if w.completed:
            return C["green"]
        if w.running:
            return C["blue"]
        if w.starred:
            return C["orange"]
        return C["border"]

    def _time_color(self):
        if self.todo_widget.completed:
            return C["strikethrough"]
        return C["text"]

    # ── 刷新显示 ──
    def refresh(self):
        w = self.todo_widget

        self.accent.configure(fg_color=self._accent_color())

        status_text, status_fg = self._status_icon()
        self.status_lbl.configure(text=status_text, text_color=status_fg)

        star_text = "★" if w.starred else ""
        star_fg = C["orange"] if w.starred else C["text_ter"]
        self.star_lbl.configure(text=star_text, text_color=star_fg)

        self.time_lbl.configure(text=fmt_time(w.get_live_elapsed()),
                                text_color=self._time_color())

        if w.completed:
            self._btn_state(self.btn_start, False)
            self._btn_state(self.btn_pause, False)
            self._btn_state(self.btn_reset, False)
        elif w.running:
            self._btn_state(self.btn_start, False)
            self._btn_state(self.btn_pause, True)
            self._btn_state(self.btn_reset, True)
        else:
            self._btn_state(self.btn_start, True)
            self._btn_state(self.btn_pause, False)
            self._btn_state(self.btn_reset, True)

    @staticmethod
    def _btn_state(btn, enabled):
        btn.configure(text_color=C["text_sec"] if enabled else C["text_ter"])

    # ── 右键菜单 ──
    def _build_menu(self):
        w = self.todo_widget
        self.menu = tk.Menu(self, tearoff=0,
                            bg=C["surface"], fg=C["text"],
                            activebackground=C["blue"], activeforeground="#FFFFFF",
                            font=(FONT, 10))
        if w.completed:
            self.menu.add_command(label="↩ 取消完成", command=self._on_toggle_complete)
        else:
            self.menu.add_command(label="✓ 标记完成", command=self._on_toggle_complete)
        if w.starred:
            self.menu.add_command(label="☆ 取消重点", command=self._on_toggle_star)
        else:
            self.menu.add_command(label="★ 标为重点", command=self._on_toggle_star)
        self.menu.add_separator()
        self.menu.add_command(label="开始计时", command=self._on_start)
        self.menu.add_command(label="暂停计时", command=self._on_pause)
        self.menu.add_command(label="重置计时", command=self._on_reset)
        self.menu.add_separator()
        self.menu.add_command(label="从面板移除", command=self._on_remove)

    # ── 按钮回调 ──
    def _on_start(self):
        self.todo_widget.start()
        self.panel._on_row_action(self.todo_widget.todo_id)

    def _on_pause(self):
        self.todo_widget.pause()
        self.panel._on_row_action(self.todo_widget.todo_id)

    def _on_reset(self):
        self.todo_widget.reset()
        self.panel._on_row_action(self.todo_widget.todo_id)

    def _on_remove(self):
        self.panel.remove_todo(self.todo_widget.todo_id)

    def _on_toggle_complete(self):
        w = self.todo_widget
        if w.completed:
            w.set_incomplete()
            self.refresh()
            self.panel._on_row_action(w.todo_id)
        else:
            w.set_complete()
            self.panel.remove_todo(w.todo_id)

    def _on_toggle_star(self):
        w = self.todo_widget
        w.starred = not w.starred
        self.refresh()
        self.panel._on_row_action(w.todo_id)
