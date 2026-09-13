import tkinter as tk
from tkinter import messagebox
import json
import os
import sys
import time
from datetime import datetime, timedelta

if getattr(sys, 'frozen', False):
    _BASE = os.path.dirname(sys.executable)
else:
    # workspace/src/main_app.py → tools/
    _BASE = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
SAVE_FILE = os.path.join(_BASE, "data", "todos.json")
ARCHIVE_FILE = os.path.join(_BASE, "data", "archive.json")

import customtkinter as ctk
from .constants import C, FONT, RADIUS, APP_VERSION
from .utils import flat_btn, get_today_str, fmt_date_cn, fmt_time
from .todo_widget import TodoWidget
from .date_picker import DatePickerDialog
from .todo_card import TodoCard, SectionHeader, EmptyHint
from .timer_panel import TimerPanel
from .tray import TrayController, autostart_enabled, set_autostart
from .tooltip import ToolTip


def _icon_btn(parent, text, cmd, tip, danger=False):
    """纯图标圆角按钮 + tooltip。danger=True 时 hover 变红。

    用 min_len=0 让短文字提示（"删除"/"标重点"…）也能正常显示。
    """
    btn = ctk.CTkButton(
        parent, text=text, command=cmd, width=28, height=26,
        fg_color="transparent",
        hover_color=(C["red_light"] if danger else C["blue_light"]),
        text_color=(C["red"] if danger else C["text_sec"]),
        corner_radius=RADIUS["button"], font=(FONT, 14), border_width=0,
    )
    ToolTip(btn, lambda: tip, min_len=0)
    return btn


class MainApp:
    # 流式渲染参数：切换视图时首屏同步出图，其余分帧补齐（防全量重排冻结 UI）
    RENDER_FIRST_BATCH = 14   # 首屏同步渲染条目数
    RENDER_BUDGET = 0.08      # 每帧渲染时间预算（秒）
    PREWARM_DELAY = 1500      # 启动后延迟多久开始预热（ms）
    PREWARM_INTERVAL = 30     # 预热节拍：每张卡片映射一拍（ms）

    def __init__(self):
        self.root = ctk.CTk()
        self.root.title("待办计时器")
        ctk.set_appearance_mode("light")
        ctk.set_default_color_theme("blue")  # 仅取结构，颜色由各控件 fg_color 覆盖
        self.root.geometry("576x624")
        self.root.minsize(560, 620)
        self.root.configure(fg_color=C["bg"])

        self.todos: dict[str, TodoWidget] = {}
        self._next_id = 0
        self._next_order = 0
        self.current_date = get_today_str()
        self.filter_state = "active"
        self._cards: list = []  # TodoCard | SectionHeader | EmptyHint
        self._card_cache: dict = {}  # todo_id -> TodoCard（跨视图复用，避免重建）
        self._deco_cache: dict = {}  # (kind, text) -> SectionHeader | EmptyHint
        self._last_plan = None       # 上次渲染计划；相同则整场刷新跳过
        self._packed_seq: list = []  # [(key, widget)] 已 pack 的渲染序列（含装饰）
        self._render_seq = 0         # 流式渲染令牌：新渲染使未完成的旧流失效
        self._stream_after_id = None
        self._scrollregion_after_id = None
        self._selected_tid: str | None = None
        self._search_term = ""
        self._undo_action = None
        self._dragging_tid: str | None = None        # 当前拖拽中的待办 id
        self._drag_ghost = None                       # 拖拽虚影 Toplevel
        self._drop_ind = None                         # 拖拽落点指示线 Toplevel
        self._autostart_intent = autostart_enabled()  # 用户期望的自启状态（持久化）

        self._save_after_id = None
        self._main_tick_id = None
        self._loading = True
        self._build_ui()
        self.timer_panel = TimerPanel(self.root, on_state_change=self._on_todo_state_change)
        self._load_todos()
        # 启动时按持久化的意图同步注册表 —— 即使关机/清理工具清掉了 Run 键也能恢复
        set_autostart(self._autostart_intent)
        self._loading = False
        self._refresh_view()
        self._prewarm_cards()

        # 系统托盘：关闭窗口=最小化到托盘，真正退出走托盘菜单
        self._tray = TrayController(
            self.root,
            on_show=self._show_window,
            on_quit=self._quit_app,
            on_toggle_autostart=self._toggle_autostart,
            on_autostart_state=lambda: self._autostart_intent,
        )
        self._tray.start()
        # pystray 线程的回调经队列投递，主线程定期 pump 取出执行（跨线程不能用 root.after）
        self.root.after(100, self._pump_tray)

        self.root.protocol("WM_DELETE_WINDOW", self._minimize_to_tray)

    def _pump_tray(self):
        """主线程轮询：取出 pystray 线程投递的回调并执行，然后重排。"""
        if self._tray is not None:
            self._tray.pump()
        self.root.after(80, self._pump_tray)

    # ── UI 构建 ────────────────────────────────────────
    def _build_ui(self):
        # ── 顶栏 ──
        hdr = ctk.CTkFrame(self.root, fg_color=C["surface"], height=40, corner_radius=0)
        hdr.pack(fill=tk.X)
        hdr.pack_propagate(False)

        ctk.CTkLabel(hdr, text="待办计时器", fg_color=C["surface"],
                     text_color=C["text"], font=(FONT, 15, "bold")).pack(
            side=tk.LEFT, padx=20, pady=7)

        # 版本号（紧贴标题右侧，小灰字）—— 安装器升级检测与这里同源
        ctk.CTkLabel(hdr, text=f"v{APP_VERSION}", fg_color=C["surface"],
                     text_color=C["text_ter"], font=(FONT, 10)).pack(
            side=tk.LEFT, padx=(0, 20), pady=7)

        self.stat_lbl = ctk.CTkLabel(hdr, text="", fg_color=C["surface"],
                                     text_color=C["text_sec"], font=(FONT, 10))
        self.stat_lbl.pack(side=tk.RIGHT, padx=20, pady=7)

        ctk.CTkFrame(self.root, fg_color=C["border"], height=1).pack(fill=tk.X)

        # ── 日期导航栏 ──
        date_bar = ctk.CTkFrame(self.root, fg_color=C["surface"])
        date_bar.pack(fill=tk.X)

        date_inner = ctk.CTkFrame(date_bar, fg_color=C["surface"])
        date_inner.pack(fill=tk.X, padx=16, pady=(6, 2))

        self.btn_prev = ctk.CTkLabel(date_inner, text="‹", fg_color=C["surface"],
                                     text_color=C["blue"],
                                     font=(FONT, 20, "bold"), cursor="hand2")
        self.btn_prev.pack(side=tk.LEFT, padx=(0, 8))
        self.btn_prev.bind("<Button-1>", lambda e: self._shift_date(-1))

        self.date_lbl = ctk.CTkLabel(date_inner, text="", fg_color=C["surface"],
                                     text_color=C["text"],
                                     font=(FONT, 14, "bold"))
        self.date_lbl.pack(side=tk.LEFT)

        self.btn_next = ctk.CTkLabel(date_inner, text="›", fg_color=C["surface"],
                                     text_color=C["blue"],
                                     font=(FONT, 20, "bold"), cursor="hand2")
        self.btn_next.pack(side=tk.LEFT, padx=(8, 0))
        self.btn_next.bind("<Button-1>", lambda e: self._shift_date(1))

        flat_btn(date_inner, "今天", self._goto_today,
                 C["blue_light"], C["blue"], font_size=9, pady=3, padx=12,
                 fg=C["blue"]).pack(side=tk.LEFT, padx=(16, 0))

        self.day_total_lbl = ctk.CTkLabel(date_inner, text="", fg_color=C["surface"],
                                          text_color=C["text_sec"], font=(FONT, 10))
        self.day_total_lbl.pack(side=tk.RIGHT)

        # ── 筛选标签栏 ──
        filter_bar = ctk.CTkFrame(self.root, fg_color=C["surface"])
        filter_bar.pack(fill=tk.X)
        filter_inner = ctk.CTkFrame(filter_bar, fg_color=C["surface"])
        filter_inner.pack(fill=tk.X, padx=16, pady=(0, 3))

        self._seg_map = {"未完成": "active", "已完成": "completed", "全部": "all"}
        self.filter_seg = ctk.CTkSegmentedButton(
            filter_inner,
            values=["未完成", "已完成", "全部"],
            command=self._on_seg_filter,
            corner_radius=RADIUS["pill"],
            fg_color=C["bg"],
            selected_color=C["blue_light"], selected_hover_color=C["border"],
            unselected_color=C["surface"], unselected_hover_color=C["bg"],
            text_color=C["text"], font=(FONT, 11),
        )
        self.filter_seg.set("未完成")
        self.filter_seg.pack(side=tk.LEFT)

        ctk.CTkFrame(self.root, fg_color=C["border"], height=1).pack(fill=tk.X)

        # ── 输入卡片 ──
        card_inp = ctk.CTkFrame(self.root, fg_color=C["surface"])
        card_inp.pack(fill=tk.X, padx=20, pady=(6, 4))

        card_inp_inner = ctk.CTkFrame(card_inp, fg_color=C["surface"])
        card_inp_inner.pack(fill=tk.X, padx=12, pady=8)

        self.entry = ctk.CTkEntry(card_inp_inner, fg_color=C["bg"], text_color=C["text"],
                                  border_color=C["border"], border_width=1,
                                  corner_radius=RADIUS["card"], font=(FONT, 12), height=36)
        self.entry.pack(side=tk.LEFT, fill=tk.X, expand=True, padx=(0, 10))
        self.entry.bind("<Return>", lambda e: self._add_todo())
        self.entry.focus_set()

        flat_btn(card_inp_inner, "＋ 添加待办", self._add_todo,
                 C["blue"], C["blue_hover"], font_size=12, pady=7, padx=20).pack(
            side=tk.LEFT)

        # ── 工具栏（主操作 + 选中操作图标栏）—— 根治横向截断 ──
        bf = ctk.CTkFrame(self.root, fg_color=C["bg"], corner_radius=0)
        bf.pack(side=tk.BOTTOM, fill=tk.X, padx=20, pady=(0, 12))

        # 主操作（文字按钮）
        row_main = ctk.CTkFrame(bf, fg_color=C["bg"])
        row_main.pack(fill=tk.X, pady=(0, 6))
        flat_btn(row_main, "▶ 全部开始", self._start_all,
                 C["green"], C["green_hover"], font_size=11).pack(side=tk.LEFT, padx=(0, 8))
        flat_btn(row_main, "⏸ 全部暂停", self._pause_all,
                 C["orange"], C["orange_hover"], font_size=11).pack(side=tk.LEFT, padx=(0, 8))
        flat_btn(row_main, "🗄 清除已完成", self._archive_completed,
                 C["surface"], C["bg"], font_size=11, fg=C["text_ter"]).pack(side=tk.RIGHT)

        # 选中操作（纯图标 + tooltip）
        row_icon = ctk.CTkFrame(bf, fg_color=C["bg"])
        row_icon.pack(fill=tk.X)

        def sep(p):
            ctk.CTkFrame(p, fg_color=C["border"], width=1, height=0).pack(
                side=tk.LEFT, fill=tk.Y, padx=6, pady=2)

        _icon_btn(row_icon, "★", self._toggle_star_selected, "标重点").pack(side=tk.LEFT, padx=2)
        _icon_btn(row_icon, "✓", self._toggle_complete_selected, "标记完成").pack(side=tk.LEFT, padx=2)
        _icon_btn(row_icon, "✎", self._edit_selected, "编辑").pack(side=tk.LEFT, padx=2)
        _icon_btn(row_icon, "📅", self._change_date, "改期").pack(side=tk.LEFT, padx=2)
        sep(row_icon)
        _icon_btn(row_icon, "↑", self._move_up_selected, "上移").pack(side=tk.LEFT, padx=2)
        _icon_btn(row_icon, "↓", self._move_down_selected, "下移").pack(side=tk.LEFT, padx=2)
        sep(row_icon)
        _icon_btn(row_icon, "🗑", self._delete_selected, "删除", danger=True).pack(side=tk.LEFT, padx=2)

        # ── 列表卡片 ──
        card_list = ctk.CTkFrame(self.root, fg_color=C["surface"])
        card_list.pack(fill=tk.BOTH, expand=True, padx=20, pady=(0, 4))

        lh = ctk.CTkFrame(card_list, fg_color=C["surface"])
        lh.pack(fill=tk.X, padx=16, pady=(8, 2))

        self.list_title_lbl = ctk.CTkLabel(lh, text="", fg_color=C["surface"], text_color=C["text"],
                                           font=(FONT, 12, "bold"))
        self.list_title_lbl.pack(side=tk.LEFT)

        # 搜索框
        self.search_var = tk.StringVar()
        self.search_var.trace_add("write", lambda *a: self._on_search_change())
        self.search_entry = ctk.CTkEntry(lh, textvariable=self.search_var,
                                         fg_color=C["bg"], text_color=C["text"],
                                         border_color=C["border"], border_width=1,
                                         corner_radius=RADIUS["button"],
                                         font=(FONT, 10), width=160)
        self.search_entry.pack(side=tk.RIGHT)

        ctk.CTkFrame(card_list, fg_color=C["border"], height=1).pack(fill=tk.X, padx=16)

        # ── 可滚动卡片区 ──
        # 列表区用浅灰底（C["bg"]），白色卡片在其上 pack 留间隙 → 卡片间有清晰分界
        lc = ctk.CTkFrame(card_list, fg_color=C["bg"])
        lc.pack(fill=tk.BOTH, expand=True, padx=4, pady=(4, 8))

        self.canvas = tk.Canvas(lc, bg=C["bg"], highlightthickness=0, bd=0)
        self.scrollbar = tk.Scrollbar(lc, orient=tk.VERTICAL,
                                      command=self.canvas.yview,
                                      bg=C["bg"], troughcolor=C["bg"],
                                      activebackground=C["blue"])
        self.canvas.configure(yscrollcommand=self.scrollbar.set)

        self.card_frame = tk.Frame(self.canvas, bg=C["bg"])
        self.card_frame_id = self.canvas.create_window((0, 0), window=self.card_frame,
                                                       anchor="nw")

        self.canvas.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        self.scrollbar.pack(side=tk.RIGHT, fill=tk.Y)

        # 让 card_frame 宽度跟随 canvas
        self.canvas.bind("<Configure>", self._on_canvas_configure)
        self.card_frame.bind("<Configure>", self._on_card_frame_configure)

        # 鼠标滚轮 — 绑定到窗口级(bind_all)：停在卡片(子控件)上时事件发给
        # 子控件而非 Canvas，Canvas 上的绑定不会触发。改用 bind_all 后任意位置
        # 都能滚；_on_mousewheel 内按所属顶层窗口做坐标守卫，避免串扰计时面板
        self.root.bind_all("<MouseWheel>", self._on_mousewheel, add="+")
        self.root.bind_all("<Button-4>", self._on_mousewheel, add="+")
        self.root.bind_all("<Button-5>", self._on_mousewheel, add="+")

        # 键盘快捷键 — 绑定到 card_frame
        self.card_frame.bind("<Up>", lambda e: self._key_move(-1))
        self.card_frame.bind("<Down>", lambda e: self._key_move(1))
        self.card_frame.bind("<space>", lambda e: self._key_toggle_complete())
        self.card_frame.bind("<Delete>", lambda e: self._delete_selected())
        self.root.bind("<Control-z>", lambda e: self._key_undo())
        self.root.bind("<Control-q>", lambda e: self._quit_app())
        # 拖拽释放安全网：鼠标释放在卡片外也能清理拖拽高亮
        self.root.bind("<ButtonRelease-1>", self._on_global_drag_release)

    def _on_canvas_configure(self, event):
        self.canvas.itemconfig(self.card_frame_id, width=event.width)

    def _on_card_frame_configure(self, _event=None):
        """card_frame 尺寸变化 → 合并刷新滚动区域。

        每张卡片增删都会触发本事件；scrollregion 逐次更新会迫使 Canvas
        整体重绘，近百张卡片的批量重排因此放大成数秒的原生开销（实测 7 倍）。
        50ms 合并后，一次批量重排只重绘一两次。"""
        if self._scrollregion_after_id:
            return
        self._scrollregion_after_id = self.canvas.after(
            50, self._apply_scrollregion)

    def _apply_scrollregion(self):
        self._scrollregion_after_id = None
        self.canvas.configure(scrollregion=self.canvas.bbox("all"))

    def _on_mousewheel(self, event):
        # 仅当光标位于主窗口内时滚动主列表，不串扰计时面板
        w = self.root.winfo_containing(event.x_root, event.y_root)
        if w is None or self._toplevel_of(w) is not self.root:
            return
        if event.num == 4:
            delta = -1
        elif event.num == 5:
            delta = 1
        else:
            delta = event.delta / 120
        self.canvas.yview_scroll(int(-delta), "units")

    @staticmethod
    def _toplevel_of(w):
        try:
            return w.winfo_toplevel()
        except Exception:
            return None

    # ── 键盘操作 ──
    def _key_move(self, direction):
        """上下键导航卡片列表"""
        cards_only = [c for c in self._cards if isinstance(c, TodoCard)]
        if not cards_only:
            return
        if not self._selected_tid:
            self._select_card(cards_only[0].todo_widget.todo_id)
            return
        for i, c in enumerate(cards_only):
            if c.todo_widget.todo_id == self._selected_tid:
                ni = max(0, min(len(cards_only) - 1, i + direction))
                self._select_card(cards_only[ni].todo_widget.todo_id)
                # 滚动确保可见
                self.canvas.yview_moveto(ni / max(1, len(cards_only)))
                return

    def _key_toggle_complete(self):
        if self._selected_tid:
            self._toggle_complete_selected()

    def _save_undo(self, action, todo_id, snapshot=None):
        self._undo_action = (action, todo_id, snapshot)

    def _key_undo(self):
        if not getattr(self, "_undo_action", None):
            return
        action, todo_id, snapshot = self._undo_action
        self._undo_action = None
        if action == "toggle_complete":
            self._toggle_complete_selected()
        elif action == "toggle_star":
            self._toggle_star_selected()
        elif action == "delete":
            if snapshot:
                self._add_todo(
                    text=snapshot.get("text"), elapsed=snapshot.get("elapsed", 0),
                    todo_id=snapshot.get("id"), visible=snapshot.get("visible", True),
                    date=snapshot.get("date"), completed=snapshot.get("completed", False),
                    order=snapshot.get("order"), starred=snapshot.get("starred", False),
                    target_seconds=snapshot.get("target_seconds"),
                    reminded=snapshot.get("reminded", False),
                )
        elif action == "edit":
            if snapshot and todo_id in self.todos:
                self.todos[todo_id].set_text(snapshot)
                self._refresh_view()
                self._select_card(todo_id)
                self._schedule_save()

    # ── 搜索 ──
    def _on_search_change(self):
        term = self.search_var.get().strip()
        if term == self._search_term:
            return
        self._search_term = term
        # 300ms 防抖后刷新
        if hasattr(self, "_search_after_id"):
            self.root.after_cancel(self._search_after_id)
        self._search_after_id = self.root.after(300, self._refresh_view)

    # ── 筛选 ──
    def _on_seg_filter(self, value):
        self._set_filter(self._seg_map[value])

    def _set_filter(self, state):
        self.filter_state = state
        # 同步分段按钮选中态（逆映射 state→中文键）
        for cn, st in self._seg_map.items():
            if st == state:
                self.filter_seg.set(cn)
                break
        self._refresh_view()

    # ── 日期 ──
    def _shift_date(self, days):
        d = datetime.strptime(self.current_date, "%Y-%m-%d") + timedelta(days=days)
        self.current_date = d.strftime("%Y-%m-%d")
        self._refresh_view()

    def _goto_today(self):
        self.current_date = get_today_str()
        self._refresh_view()

    # ── 待办 CRUD ──────────────────────────────────────
    def _add_todo(self, text=None, elapsed=0, x=None, y=None,
                  todo_id=None, visible=True, date=None, completed=False,
                  order=None, starred=False, running=False,
                  last_started_at=None, target_seconds=None, reminded=False):
        if text is None:
            text = self.entry.get().strip()
        if not text:
            return

        if todo_id is None:
            self._next_id += 1
            todo_id = str(self._next_id)
        else:
            try:
                n = int(todo_id)
                if n > self._next_id:
                    self._next_id = n
            except ValueError:
                pass

        if order is not None:
            w_order = order
            if order >= self._next_order:
                self._next_order = order + 1
        else:
            w_order = self._next_order
            self._next_order += 1

        w = TodoWidget(todo_id, text, elapsed, completed=completed,
                       order=w_order, starred=starred,
                       date=date or self.current_date,
                       target_seconds=target_seconds)
        w.reminded = bool(reminded)
        w.add_callback(lambda: self._on_todo_state_change())

        # 恢复运行状态
        if running and last_started_at and not completed:
            catchup = int(time.time() - last_started_at)
            w.elapsed = elapsed + catchup
            w._base_elapsed = w.elapsed
            w.start()

        self.todos[todo_id] = w

        # 恢复面板状态
        if visible:
            self.timer_panel.add_todo(w)

        if not self._loading:
            self._refresh_view()
            if hasattr(self, "entry") and text is not None:
                self.entry.delete(0, tk.END)
            self._schedule_save()

    def _on_todo_state_change(self):
        self._refresh_view()
        self.timer_panel.refresh_all()
        self._schedule_save()
        self._ensure_main_tick()

    # ── 主窗口轻量 Tick（仅刷新时间标签，不重建视图）──
    def _ensure_main_tick(self):
        if any(w.running for w in self.todos.values()):
            if not self._main_tick_id:
                self._main_tick()
        else:
            self._stop_main_tick()

    def _main_tick(self):
        self._main_tick_id = None
        for card in self._cards:
            if isinstance(card, TodoCard):
                w = card.todo_widget
                if w.running:
                    card.set_time_text(fmt_time(w.get_live_elapsed()))
                # 暂停/已完成的卡片也刷新 ⏰ 标签（避免显示过期倒计时）
                if hasattr(card, "update_reminder"):
                    card.update_reminder()
        # 计时提醒检查（覆盖所有运行中的待办，不限于可见卡片）
        for w in self.todos.values():
            if w.running and w.should_remind():
                w.mark_reminded()
                self._fire_reminder(w)
        if any(w.running for w in self.todos.values()):
            self._main_tick_id = self.root.after(200, self._main_tick)

    def _stop_main_tick(self):
        if self._main_tick_id:
            self.root.after_cancel(self._main_tick_id)
            self._main_tick_id = None

    # ── 视图渲染 ────────────────────────────────────────
    def _refresh_view(self):
        """渲染当前视图。先纯计算渲染计划，与上次一致则整体跳过 ——
        一次状态变更会连续触发 2~3 次本方法（observer 双订阅 + 操作后显式调用），
        计划比对使重复调用近乎零开销。"""
        plan = self._compute_plan()
        if plan == self._last_plan:
            return
        entries = plan[5]
        self._last_plan = plan

        # 选中保留：目标任务仍渲染于当前视图则保留选中，否则清空。
        # 修复：切视图后直接点 ✓ / 右键菜单时因选中丢失而静默无操作。
        card_tids = {e[1] for e in entries if e[0] == "card"}
        if self._selected_tid not in card_tids:
            self._selected_tid = None

        self._materialize(entries)

        self.date_lbl.configure(text=fmt_date_cn(self.current_date))
        titles = {"all": "全部待办", "active": "未完成", "completed": "已完成"}
        self.list_title_lbl.configure(
            text=titles.get(self.filter_state, "待办列表"))
        self.day_total_lbl.configure(text=plan[6])
        self.stat_lbl.configure(text=plan[7])

    def _materialize(self, entries):
        """把渲染计划落到控件上。

        重排一张 CTk 卡片（卸下再挂回）在原生 Tk 侧开销 ≈15ms，近百张
        全量重排是切换卡顿的根源，分三种路径规避：
        - 卡片序列不变（改文字/★/完成态等）→ 只刷新内容，一个控件都不动；
        - 纯增/纯删（完成、取消、搜索过滤、新增）→ 只增删目标卡片并重挂
          轻量装饰，其余卡片原地不动；
        - 其余（切换视图、排序变更）→ 全量重排，首屏同步渲染、剩余按时间
          预算分帧补齐，UI 不冻结。
        """
        self._render_seq += 1
        token = self._render_seq
        stream_interrupted = self._stream_after_id is not None
        self._cancel_stream()

        new_card_keys = [("card", e[1]) for e in entries if e[0] == "card"]
        old_card_keys = [k for k, _ in self._packed_seq if k[0] == "card"]
        new_deco_keys = {(e[0], e[1]) for e in entries if e[0] != "card"}
        old_deco_keys = {k for k, _ in self._packed_seq if k[0] != "card"}
        decos_changed = old_deco_keys != new_deco_keys

        if stream_interrupted:
            mode = "full"
        elif old_card_keys == new_card_keys:
            mode = "same"
        elif (len(old_card_keys) > len(new_card_keys)
                and self._is_subsequence(new_card_keys, old_card_keys)):
            mode = "delete"
        elif (len(new_card_keys) > len(old_card_keys)
                and self._is_subsequence(old_card_keys, new_card_keys)):
            mode = "insert"
        else:
            mode = "full"

        if mode == "full":
            for _, w in self._packed_seq:
                w.pack_forget()
            self._packed_seq = []
            self._cards = []
            self._gc_caches(new_deco_keys)
            # 清理可能游离在 card_frame 里的散件（防御，正常不存在）
            card_vals = set(self._card_cache.values())
            deco_vals = set(self._deco_cache.values())
            packed_ids = {id(w) for _, w in self._packed_seq}
            for child in list(self.card_frame.winfo_children()):
                if (id(child) not in packed_ids and child not in card_vals
                        and child not in deco_vals):
                    child.destroy()
            self._stream_render(entries, token)
            return

        # ── 原地更新 ──
        # 装饰（纯 tk，成本≈0）先全部卸下，随后按序重挂；废弃的销毁
        for k, w in self._packed_seq:
            if k[0] != "card":
                w.pack_forget()
        self._gc_caches(new_deco_keys)

        old_cards = [(k, w) for k, w in self._packed_seq if k[0] == "card"]
        card_widget_by_key = dict(old_cards)
        if mode == "delete":
            keep = set(new_card_keys)
            for k, w in old_cards:
                if k not in keep:
                    w.pack_forget()
        elif mode == "insert":
            successor = None
            for e in reversed([e for e in entries if e[0] == "card"]):
                key = ("card", e[1])
                if key in card_widget_by_key:
                    successor = card_widget_by_key[key]
                else:
                    w = self._obtain_card(self.todos[e[1]], e[2], e[3])
                    if successor is not None:
                        w.pack(fill=tk.X, pady=(0, 4), before=successor)
                    else:
                        w.pack(fill=tk.X, pady=(0, 4))
                    card_widget_by_key[key] = w
                    successor = w

        if mode == "same" and not decos_changed:
            # 卡片序列与装饰都没变，但数据可能已变（文字/★/完成态…）
            # → 逐卡同步内容；_sync_card 内部全带守卫，仅变化的卡片重绘
            for (k, w), e in zip(self._packed_seq, entries):
                if k[0] == "card":
                    self._sync_card(w, e)
            return

        # 重挂装饰：以已就位卡片为锚点（before/after）插入，卡片保持不动
        new_seq = []
        prev = None
        first_card_w = (card_widget_by_key[new_card_keys[0]]
                        if new_card_keys else None)
        for e in entries:
            if e[0] == "card":
                w = card_widget_by_key[("card", e[1])]
                self._sync_card(w, e)
                new_seq.append((("card", e[1]), w))
            else:
                deco = self._obtain_deco(e)
                if prev is None and first_card_w is not None:
                    deco.pack(fill=tk.X, before=first_card_w)
                elif prev is None:
                    deco.pack(fill=tk.X)
                else:
                    deco.pack(fill=tk.X, after=prev)
                prev = deco
                new_seq.append(((e[0], e[1]), deco))
            prev = new_seq[-1][1]
        self._packed_seq = new_seq
        self._cards = [w for _, w in new_seq]

    def _sync_card(self, card, e):
        """原地路径的卡片内容同步：所有 set_* 均有同值守卫，仅真正变化的卡片重绘"""
        card.todo_widget = self.todos[e[1]]
        card.set_sequence(e[2])
        card.set_show_date(e[3])
        card.set_selected(e[1] == self._selected_tid)
        card.set_dragging(False)
        card.refresh()

    def _stream_render(self, entries, token):
        """全量重排的流式渲染：首屏同步出图，其余分帧补齐，期间计划变更即作废"""
        for e in entries[:self.RENDER_FIRST_BATCH]:
            self._pack_entry(e)
        rest = entries[self.RENDER_FIRST_BATCH:]
        if not rest:
            self._stream_after_id = None
            return

        def step(remaining):
            if token != self._render_seq:
                return
            deadline = time.perf_counter() + self.RENDER_BUDGET
            i = 0
            while i < len(remaining) and (i == 0 or time.perf_counter() < deadline):
                self._pack_entry(remaining[i])
                i += 1
            remaining = remaining[i:]
            if remaining:
                self._stream_after_id = self.root.after(16, step, remaining)
            else:
                self._stream_after_id = None

        self._stream_after_id = self.root.after(16, step, rest)

    def _pack_entry(self, e):
        if e[0] == "card":
            card = self._obtain_card(self.todos[e[1]], e[2], e[3])
            card.pack(fill=tk.X, pady=(0, 4))
            self._packed_seq.append((("card", e[1]), card))
            self._cards.append(card)
        else:
            deco = self._obtain_deco(e)
            deco.pack(fill=tk.X)
            self._packed_seq.append(((e[0], e[1]), deco))
            self._cards.append(deco)

    def _cancel_stream(self):
        if self._stream_after_id is not None:
            self.root.after_cancel(self._stream_after_id)
            self._stream_after_id = None

    def _gc_caches(self, new_deco_keys):
        """清理已删除待办的卡片缓存与不再使用的装饰缓存"""
        for tid in list(self._card_cache.keys()):
            if tid not in self.todos:
                self._card_cache[tid].destroy()
                del self._card_cache[tid]
        for key in list(self._deco_cache.keys()):
            if key not in new_deco_keys:
                self._deco_cache.pop(key).destroy()

    @staticmethod
    def _is_subsequence(needles, haystack):
        """needles 能否按序嵌入 haystack（识别纯增/纯删的渲染计划）"""
        it = iter(haystack)
        return all(any(x == y for y in it) for x in needles)

    def _compute_plan(self):
        """纯计算当前视图的渲染计划，不触碰任何控件。

        返回元组 (filter_state, current_date, search, n_todos, fingerprint,
        entries, day_total_text, stat_text)。entries 为不可变序列：
        ("hdr"|"sub"|"empty", 文本) 与 ("card", todo_id, 序号, 显示日期)。
        fingerprint 覆盖所有影响渲染的数据（含不走 _fire_change 的
        starred/_order 直接赋值），任一变化都会使计划失效。
        """
        st = self._search_term.lower()

        def _matches(w):
            return not st or st in w.text.lower()

        today_str = self.current_date
        entries = []
        day_total_text = ""
        order_key = lambda w: (not getattr(w, "starred", False),
                               getattr(w, "_order", 0))

        if self.filter_state == "active":
            today_items, cross_items = [], []
            for w in self.todos.values():
                if w.completed or not _matches(w):
                    continue
                w_date = getattr(w, "_date", get_today_str())
                if w_date == today_str:
                    today_items.append(w)
                elif w_date < today_str:
                    cross_items.append(w)

            today_items.sort(key=order_key)
            cross_items.sort(key=order_key)

            day_total = 0
            if today_items:
                entries.append(("hdr", "▎今日待办"))
            for seq, w in enumerate(today_items, 1):
                day_total += w.elapsed
                entries.append(("card", w.todo_id, seq, False))

            if cross_items:
                entries.append(("hdr", f"▎跨日未完成 ({len(cross_items)}项)"))
                groups = {}
                for w in cross_items:
                    groups.setdefault(getattr(w, "_date", ""), []).append(w)
                single = len(groups) == 1
                for dk in sorted(groups.keys()):
                    group_items = groups[dk]
                    if not single:
                        entries.append(
                            ("sub", f"  {fmt_date_cn(dk)} · {len(group_items)}项"))
                    for seq, w in enumerate(group_items, 1):
                        entries.append(("card", w.todo_id, seq, single))

            if not today_items and not cross_items:
                entries.append(("empty", "暂无待办"))
            day_total_text = f"今日合计 {fmt_time(day_total)}  ·  {len(today_items)} 项"

        elif self.filter_state == "completed":
            # 已完成视图：所有完成项按日期倒序分组（跨日完成归到完成当天）
            all_completed = [w for w in self.todos.values()
                             if w.completed and _matches(w)]
            all_completed.sort(key=lambda w: (
                getattr(w, "_date", ""), not getattr(w, "starred", False),
                getattr(w, "_order", 0)))
            if all_completed:
                groups = {}
                for w in all_completed:
                    groups.setdefault(getattr(w, "_date", get_today_str()),
                                      []).append(w)
                for dk in sorted(groups.keys(), reverse=True):
                    group_items = groups[dk]
                    entries.append(
                        ("hdr", f"▎{fmt_date_cn(dk)} · {len(group_items)}项"))
                    for seq, w in enumerate(group_items, 1):
                        entries.append(("card", w.todo_id, seq, False))
            else:
                entries.append(("empty", "暂无已完成待办"))
            day_total_text = f"已完成 {len(all_completed)} 项"

        else:
            # 全部视图：所有待办按日期倒序分组
            all_todos = [w for w in self.todos.values() if _matches(w)]
            all_todos.sort(key=lambda w: (
                getattr(w, "_date", ""), not getattr(w, "starred", False),
                w.completed, getattr(w, "_order", 0)))
            if all_todos:
                groups = {}
                for w in all_todos:
                    groups.setdefault(getattr(w, "_date", get_today_str()),
                                      []).append(w)
                day_total = 0
                today = get_today_str()
                for dk in sorted(groups.keys(), reverse=True):
                    group_items = groups[dk]
                    label = (f"▎今日 · {len(group_items)}项" if dk == today
                             else f"▎{fmt_date_cn(dk)} · {len(group_items)}项")
                    entries.append(("hdr", label))
                    for seq, w in enumerate(group_items, 1):
                        if not w.completed:
                            day_total += w.elapsed
                        entries.append(("card", w.todo_id, seq, False))
            else:
                entries.append(("empty", "暂无待办"))
            active_count = sum(1 for w in all_todos if not w.completed)
            day_total_text = f"合计 {fmt_time(day_total)}  ·  {active_count} 项"

        total = len(self.todos)
        active = sum(1 for w in self.todos.values() if not w.completed)
        done = total - active
        running = sum(1 for w in self.todos.values() if w.running)
        stat_text = (f"全部 {total} 项 · 未完成 {active} · 已完成 {done}"
                     f" · 运行中 {running}")

        fp = tuple((w, w._version, w.starred, w._order, w.completed,
                    w.is_visible(), getattr(w, "_date", ""), w.elapsed)
                   for w in self.todos.values())
        return (self.filter_state, self.current_date, self._search_term,
                total, fp, tuple(entries), day_total_text, stat_text)

    def _obtain_deco(self, entry):
        """从缓存取或新建分组标题/空提示（跨刷新复用，避免反复重建 CTk 控件）"""
        kind, text = entry[0], entry[1]
        key = (kind, text)
        deco = self._deco_cache.get(key)
        if deco is None:
            if kind == "empty":
                deco = EmptyHint(self.card_frame, text)
            else:
                deco = SectionHeader(self.card_frame, text,
                                     compact=(kind == "sub"))
            self._deco_cache[key] = deco
        return deco

    def _prewarm_cards(self):
        """启动后空闲时段预热卡片缓存。

        卡片的首次映射（canvas 初始化 + 尺寸协商）远贵于重映射，是首次切换
        视图卡顿的主因。这里逐张 place 到可视区外完成首次映射，下一拍再卸下，
        把这笔一次性成本摊在启动空闲期；此后切换视图全是热路径。"""
        pending = [w for w in self.todos.values()
                   if w.todo_id not in self._card_cache]
        mapped: list = []

        def step():
            for c in mapped:
                c.place_forget()
            mapped.clear()
            if not pending:
                return
            w = pending.pop(0)
            tid = w.todo_id
            if tid in self.todos and tid not in self._card_cache:
                card = self._obtain_card(w, seq=0, show_date=False)
                card.place(x=-20000, y=0)   # 映射以触发首次绘制, 不可见
                mapped.append(card)
            self._prewarm_after_id = self.root.after(self.PREWARM_INTERVAL, step)

        self._prewarm_after_id = self.root.after(self.PREWARM_DELAY, step)

    def _obtain_card(self, w, seq, show_date):
        """从缓存取或新建卡片。跨视图复用，避免每次切换都重建全部控件（CTk 控件
        构造较慢，是切换卡顿的根因）。复用时仅重新 pack/排序；set_*/refresh
        内部均有同值守卫，数据未变化的卡片零重绘。"""
        tid = w.todo_id
        card = self._card_cache.get(tid)
        if card is None:
            card = TodoCard(self.card_frame, w, seq=seq, show_date=show_date)
            card.callback_click = self._on_card_click
            card.callback_double = self._on_card_double
            card.callback_right = self._on_card_right_click
            card.callback_drag_start = self._on_drag_start
            card.callback_drag_move = self._on_drag_move
            card.callback_drag_drop = self._on_drag_drop
            self._card_cache[tid] = card
        else:
            self._sync_card(card, ("card", tid, seq, show_date))
        return card

    # ── 卡片交互回调 ────────────────────────────────────
    def _on_card_click(self, tid):
        self._select_card(tid)

    def _on_card_double(self, tid):
        if tid in self.todos:
            w = self.todos[tid]
            if w.completed:
                return
            if not w.is_visible():
                self.timer_panel.add_todo(w)
            self.timer_panel.show()
            self._refresh_view()
            self._schedule_save()

    def _on_card_right_click(self, tid, event):
        self._select_card(tid)
        w = self.todos.get(tid)
        if not w:
            return

        menu = tk.Menu(self.root, tearoff=0,
                       bg=C["surface"], fg=C["text"],
                       activebackground=C["blue"], activeforeground="#FFFFFF",
                       font=(FONT, 10))
        menu.add_command(label="🔍 显示 / 隐藏悬浮窗", command=self._toggle_visible)

        if w.completed:
            menu.add_command(label="↩ 取消完成", command=self._toggle_complete_selected)
        else:
            menu.add_command(label="✓ 标记完成", command=self._toggle_complete_selected)

        if w.starred:
            menu.add_command(label="☆ 取消重点", command=self._toggle_star_selected)
        else:
            menu.add_command(label="★ 标为重点", command=self._toggle_star_selected)

        if w.target_seconds:
            menu.add_command(label="⏰ 清除提醒", command=lambda: (w.clear_target(), self._refresh_view(),
                             self._select_card(tid), self._schedule_save()))
        menu.add_command(label="⏰ 设置提醒", command=self._set_reminder)

        menu.add_separator()
        menu.add_command(label="↑ 上移", command=self._move_up_selected)
        menu.add_command(label="↓ 下移", command=self._move_down_selected)
        menu.add_separator()
        menu.add_command(label="✎ 编辑", command=self._edit_selected)
        menu.add_command(label="📅 更改日期", command=self._change_date)
        menu.add_separator()
        menu.add_command(label="🗑 删除", command=self._delete_selected)
        menu.post(event.x_root, event.y_root)

    def _select_card(self, tid):
        # 取消旧选中
        if self._selected_tid:
            for card in self._cards:
                if isinstance(card, TodoCard) and card.todo_widget.todo_id == self._selected_tid:
                    card.set_selected(False)
                    break
        self._selected_tid = tid
        # 选中新
        if tid:
            for card in self._cards:
                if isinstance(card, TodoCard) and card.todo_widget.todo_id == tid:
                    card.set_selected(True)
                    break

    def _get_selected_id(self):
        return self._selected_tid

    # ── 排序 ──
    def _move_up_selected(self):
        tid = self._get_selected_id()
        if not tid:
            return
        self._move_order(tid, -1)

    def _move_down_selected(self):
        tid = self._get_selected_id()
        if not tid:
            return
        self._move_order(tid, 1)

    def _move_order(self, tid, direction):
        # 找到当前 card 在 _cards 中的位置
        cur_idx = None
        for i, card in enumerate(self._cards):
            if isinstance(card, TodoCard) and card.todo_widget.todo_id == tid:
                cur_idx = i
                break
        if cur_idx is None:
            return

        # 找相邻的 TodoCard
        target_idx = cur_idx + direction
        while 0 <= target_idx < len(self._cards):
            if isinstance(self._cards[target_idx], TodoCard):
                break
            target_idx += direction

        if not (0 <= target_idx < len(self._cards)):
            return
        target_card = self._cards[target_idx]
        if not isinstance(target_card, TodoCard):
            return

        # 交换 order
        w1 = self.todos[tid]
        w2 = self.todos[target_card.todo_widget.todo_id]
        w1._order, w2._order = w2._order, w1._order

        self._refresh_view()
        self._select_card(tid)
        self._schedule_save()

    # ── 操作 ──
    def _toggle_visible(self):
        tid = self._get_selected_id()
        if not tid:
            return
        w = self.todos[tid]
        if w.is_visible():
            self.timer_panel.remove_todo(tid)
        else:
            self.timer_panel.add_todo(w)
        self._refresh_view()
        self._select_card(tid)
        self._schedule_save()

    def _toggle_star_selected(self):
        tid = self._get_selected_id()
        if not tid:
            return
        self._save_undo("toggle_star", tid)
        w = self.todos[tid]
        w.starred = not w.starred
        self._refresh_view()
        self._select_card(tid)
        self._schedule_save()

    def _toggle_complete_selected(self):
        tid = self._get_selected_id()
        if not tid:
            return
        w = self.todos[tid]
        self._save_undo("toggle_complete", tid)
        if w.completed:
            w.set_incomplete()
        else:
            w.set_complete()
        self._refresh_view()
        self._select_card(tid)
        self._schedule_save()

    def _edit_selected(self):
        tid = self._get_selected_id()
        if not tid:
            return
        w = self.todos[tid]

        dlg = ctk.CTkToplevel(self.root, fg_color=C["surface"])
        dlg.title("编辑待办")
        dlg.geometry("420x150")
        dlg.resizable(False, False)
        dlg.transient(self.root)
        dlg.grab_set()

        ctk.CTkLabel(dlg, text="编辑待办事项", fg_color="transparent", text_color=C["text"],
                     font=(FONT, 13, "bold")).pack(pady=(14, 8))

        entry = ctk.CTkEntry(dlg, fg_color=C["bg"], text_color=C["text"],
                             border_color=C["border"], border_width=1,
                             corner_radius=RADIUS["card"], font=(FONT, 12), height=36)
        entry.pack(fill=tk.X, padx=20)
        entry.insert(0, w.text)
        entry.select_range(0, tk.END)
        entry.focus_set()

        def save():
            new_text = entry.get().strip()
            if new_text:
                self._save_undo("edit", tid, w.text)
                w.set_text(new_text)
                self._refresh_view()
                self._select_card(tid)
                self._schedule_save()
            dlg.destroy()

        entry.bind("<Return>", lambda e: save())

        btns = ctk.CTkFrame(dlg, fg_color="transparent")
        btns.pack(pady=(12, 0))
        flat_btn(btns, "保存", save, C["blue"], C["blue_hover"],
                 font_size=10, pady=5, padx=24).pack(side=tk.LEFT, padx=6)
        flat_btn(btns, "取消", dlg.destroy, C["text_sec"], C["text_ter"],
                 font_size=10, pady=5, padx=24).pack(side=tk.LEFT, padx=6)

        dlg.wait_window()

    def _change_date(self):
        tid = self._get_selected_id()
        if not tid:
            return
        w = self.todos[tid]
        current = getattr(w, "_date", get_today_str())

        def set_date(new_date):
            w.set_date(new_date)
            self._refresh_view()
            self._select_card(tid)

        DatePickerDialog(self.root, current, set_date)

    def _delete_selected(self):
        tid = self._get_selected_id()
        if not tid:
            return
        w = self.todos.get(tid)
        if not w:
            return
        if w.elapsed > 0 and not messagebox.askyesno(
            "确认删除", f"确定删除「{w.text}」吗？\n已记录 {fmt_time(w.elapsed)}。",
            parent=self.root):
            return
        # 保存撤销快照
        snapshot = {
            "id": w.todo_id, "text": w.text, "elapsed": w.elapsed,
            "completed": w.completed, "visible": w.is_visible(),
            "date": getattr(w, "_date", get_today_str()),
            "order": w._order, "starred": w.starred,
            "target_seconds": w.target_seconds, "reminded": w.reminded,
        }
        self._save_undo("delete", tid, snapshot)
        if self.timer_panel.has_todo(tid):
            self.timer_panel.remove_todo(tid)
        self.todos.pop(tid, None)
        w.destroy()
        self._selected_tid = None
        self._refresh_view()
        self._schedule_save()

    def _archive_completed(self):
        """归档所有已完成待办到 archive.json，并从主列表中移除"""
        completed_ids = [tid for tid, w in self.todos.items() if w.completed]
        if not completed_ids:
            return
        count = len(completed_ids)
        if not messagebox.askyesno(
            "确认归档", f"将 {count} 条已完成待办移到归档文件？\n归档后可在 data/archive.json 中查看。",
            parent=self.root):
            return

        # 写入归档
        archive = []
        if os.path.exists(ARCHIVE_FILE):
            try:
                with open(ARCHIVE_FILE, "r", encoding="utf-8") as f:
                    archive = json.load(f)
            except Exception:
                archive = []

        for tid in completed_ids:
            w = self.todos[tid]
            d = w.to_dict()
            d["date"] = getattr(w, "_date", get_today_str())
            d["archived_at"] = get_today_str()
            archive.append(d)
            if self.timer_panel.has_todo(tid):
                self.timer_panel.remove_todo(tid)
            self.todos.pop(tid, None)

        try:
            os.makedirs(os.path.dirname(ARCHIVE_FILE), exist_ok=True)
            with open(ARCHIVE_FILE, "w", encoding="utf-8") as f:
                json.dump(archive, f, ensure_ascii=False, indent=2)
        except Exception:
            import traceback
            traceback.print_exc()

        self._selected_tid = None
        self._refresh_view()
        self._schedule_save()

    def _start_all(self):
        saved_tid = self._selected_tid
        for card in self._cards:
            if isinstance(card, TodoCard):
                w = card.todo_widget
                if not w.completed:
                    if not w.is_visible():
                        self.timer_panel.add_todo(w)
                    w.start()
        self.timer_panel.show()
        self._refresh_view()
        if saved_tid:
            self._select_card(saved_tid)

    def _pause_all(self):
        saved_tid = self._selected_tid
        for card in self._cards:
            if isinstance(card, TodoCard):
                card.todo_widget.pause()
        self._refresh_view()
        if saved_tid:
            self._select_card(saved_tid)

    # ── 拖拽排序（组内）──────────────────────────────────
    def _on_drag_start(self, tid):
        self._dragging_tid = tid
        for c in self._cards:
            if isinstance(c, TodoCard) and c.todo_widget.todo_id == tid:
                c.set_dragging(True)
                break
        self._create_drag_ghost(tid)

    def _create_drag_ghost(self, tid):
        """创建跟随光标的拖拽虚影，让用户清楚知道正在拖动哪一项。"""
        if tid not in self.todos:
            return
        self._destroy_drag_ghost()
        g = tk.Toplevel(self.root)
        g.overrideredirect(True)
        g.attributes("-topmost", True)
        try:
            g.attributes("-alpha", 0.85)   # 半透明，营造“虚影”感
        except Exception:
            pass
        g.configure(bg=C["blue"], highlightthickness=0, bd=0)
        body = ctk.CTkFrame(g, fg_color=C["surface"], corner_radius=RADIUS["card"])
        body.pack(fill=tk.BOTH, expand=True, padx=2)  # 2px 蓝边（圆角卡片浮于蓝边之上）
        text = self.todos[tid].text
        lbl = ctk.CTkLabel(body, text=f"✥ {text}", fg_color="transparent",
                           text_color=C["text"], font=(FONT, 10),
                           anchor="w", padx=10, pady=6)
        lbl.pack()
        # 立即定位到光标处，避免虚影先在 (0,0) 闪现
        try:
            g.geometry(f"+{self.root.winfo_pointerx() + 14}"
                       f"+{self.root.winfo_pointery() + 10}")
        except Exception:
            pass
        self._drag_ghost = g

    def _destroy_drag_ghost(self):
        if self._drag_ghost is not None:
            try:
                self._drag_ghost.destroy()
            except Exception:
                pass
            self._drag_ghost = None

    def _on_drag_move(self, tid, y_root):
        """移动拖拽虚影跟随光标，并实时刷新落点指示线。"""
        if self._drag_ghost is not None:
            x = self.root.winfo_pointerx() + 14
            y = self.root.winfo_pointery() + 10
            try:
                self._drag_ghost.geometry(f"+{x}+{y}")
            except Exception:
                pass
        # 实时落点指示线（与 drop 用同一套计算，所见即所落）
        res = self._drop_insert_index(self._drag_group_cards(tid), tid, y_root)
        if res is None:
            self._hide_drop_indicator()
        else:
            _, _, iy = res
            self._show_drop_indicator(iy, tid)

    def _drag_group_cards(self, dragged_tid):
        """被拖项所在 _date 分组的卡片列表（视觉顺序）。"""
        if dragged_tid not in self.todos:
            return []
        d_date = getattr(self.todos[dragged_tid], "_date", get_today_str())
        return [c for c in self._cards
                if isinstance(c, TodoCard)
                and getattr(c.todo_widget, "_date", "") == d_date]

    def _drop_insert_index(self, group_cards, dragged_tid, y_root):
        """返回 (remaining, insert_at, indicator_y_root) 或 None（无效落点/组太小）。

        落点必须在分组垂直跨度内，否则视为跨组 → 弹回（与 _reorder_in_group 一致）。
        """
        if len(group_cards) < 2:
            return None
        if next((c for c in group_cards
                 if c.todo_widget.todo_id == dragged_tid), None) is None:
            return None
        remaining = [c for c in group_cards if c.todo_widget.todo_id != dragged_tid]
        gtop = group_cards[0].winfo_rooty()
        last = group_cards[-1]
        gbot = last.winfo_rooty() + (last.winfo_height() or 1)
        if y_root < gtop - 2 or y_root > gbot + 2:
            return None
        insert_at = len(remaining)
        for i, c in enumerate(remaining):
            cy = c.winfo_rooty()
            ch = c.winfo_height() or 1
            if y_root < cy + ch // 2:
                insert_at = i
                break
        if insert_at >= len(remaining):
            prev = remaining[-1]
            iy = prev.winfo_rooty() + (prev.winfo_height() or 1)
        else:
            iy = remaining[insert_at].winfo_rooty()
        return remaining, insert_at, iy

    def _show_drop_indicator(self, y_root, dragged_tid):
        """显示/移动一条蓝色细线，标记当前松手后的插入位置。"""
        if self._drop_ind is None:
            ind = tk.Toplevel(self.root)
            ind.overrideredirect(True)
            ind.attributes("-topmost", True)
            ind.configure(bg=C["blue"], highlightthickness=0, bd=0)
            self._drop_ind = ind
        group = self._drag_group_cards(dragged_tid)
        x = group[0].winfo_rootx() if group else self.root.winfo_pointerx()
        w = max((c.winfo_width() for c in group), default=240)
        try:
            self._drop_ind.geometry(f"{max(w, 80)}x3+{x}+{y_root - 1}")
            self._drop_ind.deiconify()
            self._drop_ind.lift()
        except Exception:
            pass

    def _hide_drop_indicator(self):
        if self._drop_ind is not None:
            try:
                self._drop_ind.withdraw()
            except Exception:
                pass

    def _destroy_drop_indicator(self):
        if self._drop_ind is not None:
            try:
                self._drop_ind.destroy()
            except Exception:
                pass
            self._drop_ind = None

    def _on_drag_drop(self, tid, y_root):
        self._clear_drag_highlight()
        self._reorder_in_group(tid, y_root)

    def _on_global_drag_release(self, event):
        """安全网：鼠标释放在卡片外（grab 未投递）也能清理拖拽高亮"""
        self._clear_drag_highlight()

    def _clear_drag_highlight(self):
        self._destroy_drag_ghost()
        self._destroy_drop_indicator()
        if self._dragging_tid:
            for c in self._cards:
                if (isinstance(c, TodoCard)
                        and c.todo_widget.todo_id == self._dragging_tid):
                    c.set_dragging(False)
                    break
            self._dragging_tid = None

    def _reorder_in_group(self, dragged_tid, y_root):
        """在同 _date 分组内重排 _order；跨组落点 → 不变（弹回）。"""
        if dragged_tid not in self.todos:
            return
        group_cards = self._drag_group_cards(dragged_tid)
        res = self._drop_insert_index(group_cards, dragged_tid, y_root)
        if res is None:
            return
        remaining, insert_at, _ = res

        # 被拖卡片在原组（含自身、视觉顺序）中的索引，用于判断位置是否变化
        cur_idx = next((i for i, c in enumerate(group_cards)
                        if c.todo_widget.todo_id == dragged_tid), 0)
        if insert_at == cur_idx:
            return

        dragged = self.todos[dragged_tid]
        ordered = [c.todo_widget for c in remaining]
        ordered.insert(insert_at, dragged)

        # 用组内现有 _order 升序值重新分配（保持间距，避免重复值）
        order_vals = sorted(getattr(w, "_order", 0) for w in ordered)
        for w, ov in zip(ordered, order_vals):
            w._order = ov

        self._refresh_view()
        self._select_card(dragged_tid)
        self._schedule_save()

    # ── 计时提醒 ──────────────────────────────────────────
    def _center_over(self, dlg, w, h):
        """把对话框居中到主窗口上方（主窗口不可见时退化为屏幕居中）。"""
        try:
            rw = self.root.winfo_width()
            rh = self.root.winfo_height()
            rx = self.root.winfo_rootx()
            ry = self.root.winfo_rooty()
            if rw > 1 and rh > 1:
                x = rx + (rw - w) // 2
                y = ry + (rh - h) // 2
            else:
                raise ValueError
        except Exception:
            x = (dlg.winfo_screenwidth() - w) // 2
            y = (dlg.winfo_screenheight() - h) // 2
        dlg.geometry(f"{w}x{h}+{max(0, x)}+{max(0, y)}")

    def _fire_reminder(self, w):
        """目标时长达成：声音 + 托盘通知 + 置顶弹窗"""
        try:
            import winsound
            winsound.Beep(880, 500)
        except Exception:
            pass
        if self._tray is not None:
            self._tray.notify("计时提醒", f"「{w.text}」已达 {fmt_time(w.target_seconds)}")

        dlg = ctk.CTkToplevel(self.root, fg_color=C["surface"])
        dlg.title("计时提醒")
        dlg.geometry("400x190")
        dlg.attributes("-topmost", True)
        dlg.transient(self.root)
        self._center_over(dlg, 400, 190)
        dlg.grab_set()
        dlg.focus_force()

        ctk.CTkLabel(dlg, text="⏰ 计时目标达成", fg_color="transparent",
                     text_color=C["orange"],
                     font=(FONT, 15, "bold")).pack(pady=(20, 8))
        ctk.CTkLabel(dlg, text=f"「{w.text}」\n已达 {fmt_time(w.target_seconds)}",
                     fg_color="transparent", text_color=C["text"], font=(FONT, 11),
                     justify="center").pack(pady=(0, 14))

        btns = ctk.CTkFrame(dlg, fg_color="transparent")
        btns.pack()

        def close():
            dlg.destroy()

        flat_btn(btns, "继续计时", lambda: (w.start(), close()),
                 C["green"], C["green_hover"], font_size=10, pady=5, padx=14).pack(
            side=tk.LEFT, padx=3)
        flat_btn(btns, "暂停", lambda: (w.pause(), close()),
                 C["orange"], C["orange_hover"], font_size=10, pady=5, padx=14).pack(
            side=tk.LEFT, padx=3)
        flat_btn(btns, "标记完成", lambda: (w.set_complete(), self._refresh_view(), close()),
                 C["blue"], C["blue_hover"], font_size=10, pady=5, padx=14).pack(
            side=tk.LEFT, padx=3)
        flat_btn(btns, "关闭", close, C["text_sec"], C["text_ter"],
                 font_size=10, pady=5, padx=14).pack(side=tk.LEFT, padx=3)

        self._show_window()  # 唤起窗口让用户看到弹窗

    def _set_reminder(self):
        """为目标待办设置/清除计时提醒"""
        tid = self._get_selected_id()
        if not tid:
            return
        w = self.todos[tid]

        dlg = ctk.CTkToplevel(self.root, fg_color=C["surface"])
        dlg.title("设置计时提醒")
        dlg.geometry("320x220")
        dlg.resizable(False, False)
        dlg.transient(self.root)
        dlg.attributes("-topmost", True)   # 必须置顶，否则可能被主窗口盖住而 grab 卡死
        self._center_over(dlg, 320, 220)
        dlg.grab_set()
        dlg.focus_force()

        ctk.CTkLabel(dlg, text="⏰ 目标时长提醒", fg_color="transparent",
                     text_color=C["text"],
                     font=(FONT, 13, "bold")).pack(pady=(16, 2))
        cur = f"当前：{fmt_time(w.target_seconds)}" if w.target_seconds else "当前：未设置"
        ctk.CTkLabel(dlg, text=cur, fg_color="transparent", text_color=C["text_sec"],
                     font=(FONT, 9)).pack(pady=(0, 10))

        def apply_minutes(m):
            w.set_target(m * 60)
            self._refresh_view()
            self._select_card(tid)
            self._schedule_save()
            dlg.destroy()

        presets = ctk.CTkFrame(dlg, fg_color="transparent")
        presets.pack()
        for m in (5, 15, 25, 60):
            flat_btn(presets, f"{m} 分钟", lambda mm=m: apply_minutes(mm),
                     C["blue_light"], C["blue"], font_size=10, pady=4, padx=12,
                     fg=C["blue"]).pack(side=tk.LEFT, padx=3)

        custom = ctk.CTkFrame(dlg, fg_color="transparent")
        custom.pack(pady=(10, 0))

        def apply_entry():
            try:
                m = int(ent.get().strip())
                if m > 0:
                    apply_minutes(m)
            except ValueError:
                pass

        ent = ctk.CTkEntry(custom, fg_color=C["bg"], text_color=C["text"],
                           border_color=C["border"], border_width=1,
                           corner_radius=RADIUS["button"], font=(FONT, 10), width=60)
        ent.pack(side=tk.LEFT, padx=(0, 4))
        flat_btn(custom, "自定义", apply_entry, C["blue"], C["blue_hover"],
                 font_size=9, pady=3, padx=10).pack(side=tk.LEFT, padx=2)
        flat_btn(custom, "清除", lambda: (w.clear_target(), self._refresh_view(),
                 self._select_card(tid), self._schedule_save(), dlg.destroy()),
                 C["text_ter"], C["red_light"], font_size=9, pady=3, padx=10).pack(
            side=tk.LEFT, padx=2)
        ent.bind("<Return>", lambda e: apply_entry())

        dlg.wait_window()

    # ── 持久化 ──────────────────────────────────────────
    def _schedule_save(self):
        """延迟 300ms 合并保存，避免频繁写盘"""
        if self._loading:
            return
        if self._save_after_id:
            self.root.after_cancel(self._save_after_id)
        self._save_after_id = self.root.after(300, self._do_save)

    def _do_save(self):
        self._save_after_id = None
        self._save()

    def _save(self):
        data = {
            "panel_x": self.timer_panel.get_position()[0],
            "panel_y": self.timer_panel.get_position()[1],
            "autostart": self._autostart_intent,
            "todos": [],
        }
        for w in self.todos.values():
            d = w.to_dict()
            d["date"] = getattr(w, "_date", get_today_str())
            data["todos"].append(d)
        try:
            os.makedirs(os.path.dirname(SAVE_FILE), exist_ok=True)
            tmp = SAVE_FILE + ".tmp"
            with open(tmp, "w", encoding="utf-8") as f:
                json.dump(data, f, ensure_ascii=False, indent=2)
            os.replace(tmp, SAVE_FILE)  # 原子替换，防止写一半崩溃
            # 滚动备份
            bak = SAVE_FILE + ".bak"
            try:
                with open(SAVE_FILE, "r", encoding="utf-8") as src:
                    with open(bak, "w", encoding="utf-8") as dst:
                        dst.write(src.read())
            except Exception:
                pass
        except Exception:
            import traceback
            traceback.print_exc()

    def _load_todos(self):
        if not os.path.exists(SAVE_FILE):
            return
        try:
            with open(SAVE_FILE, "r", encoding="utf-8") as f:
                raw = json.load(f)
        except Exception:
            import traceback
            traceback.print_exc()
            bak = SAVE_FILE + ".bak"
            if os.path.exists(bak):
                try:
                    with open(bak, "r", encoding="utf-8") as f:
                        raw = json.load(f)
                except Exception:
                    return
            else:
                return

        # 兼容旧格式（纯数组）和新格式（含 panel 位置）
        if isinstance(raw, list):
            data = raw
            panel_x, panel_y = 100, 100
        else:
            data = raw.get("todos", [])
            panel_x = raw.get("panel_x", 100)
            panel_y = raw.get("panel_y", 100)
            if "autostart" in raw:
                self._autostart_intent = bool(raw["autostart"])

        self.timer_panel.set_position(panel_x, panel_y)

        for item in data:
            self._add_todo(
                text=item["text"],
                elapsed=item.get("elapsed", 0),
                x=item.get("x"),
                y=item.get("y"),
                todo_id=item.get("id"),
                visible=item.get("visible", True),
                date=item.get("date", get_today_str()),
                completed=item.get("completed", False),
                order=item.get("order"),
                starred=item.get("starred", False),
                running=item.get("running", False),
                last_started_at=item.get("last_started_at"),
                target_seconds=item.get("target_seconds"),
                reminded=item.get("reminded", False),
            )

    # ── 系统托盘 / 窗口控制 ────────────────────────────
    def _tray_available(self):
        return self._tray is not None and getattr(self._tray, "_icon", None) is not None

    def _minimize_to_tray(self):
        """关闭按钮 → 最小化到托盘（不退出）。无托盘时退化为真正退出。

        计时面板是独立的 overrideredirect Toplevel（非 transient），root.withdraw()
        不会影响它 —— 所以悬浮窗在主窗口最小化时保持可见。
        """
        if not self._tray_available():
            self._quit_app()
            return
        # 刷新待落盘的改动，避免最小化后被杀进程丢数据
        if self._save_after_id:
            self.root.after_cancel(self._save_after_id)
            self._save_after_id = None
        self._save()
        self.root.withdraw()
        self._tray.notify("待办计时器", "已最小化到托盘，点击图标恢复")

    def _show_window(self):
        """从托盘恢复窗口"""
        self.root.deiconify()
        self.root.lift()
        self.root.focus_force()

    def _toggle_autostart(self):
        # 以持久化的“用户意图”为准翻转，再写入注册表并落盘
        self._autostart_intent = not self._autostart_intent
        set_autostart(self._autostart_intent)
        # 强制刷新托盘菜单（pystray 会缓存菜单文字，否则重开仍显示旧勾选态）
        if self._tray is not None:
            self._tray.refresh_menu()
        if self._autostart_intent:
            self._toast("✓ 已开启开机自启", "green")
        else:
            self._toast("已关闭开机自启", "text_sec")
        self._schedule_save()

    def _toast(self, text, color_key="blue"):
        """右下角短暂提示条，~2 秒后自动消失 —— 给托盘菜单操作即时可见的反馈。"""
        t = tk.Toplevel(self.root)
        t.overrideredirect(True)
        t.attributes("-topmost", True)
        try:
            t.attributes("-alpha", 0.95)
        except Exception:
            pass
        bg = C[color_key]
        t.configure(bg=bg)
        ctk.CTkLabel(t, text=text, fg_color=bg, text_color="white",
                     corner_radius=6, font=(FONT, 11, "bold"),
                     padx=22, pady=11).pack()
        t.update_idletasks()
        w = t.winfo_reqwidth()
        h = t.winfo_reqheight()
        t.geometry(f"+{t.winfo_screenwidth() - w - 24}"
                   f"+{t.winfo_screenheight() - h - 64}")
        t.after(2000, t.destroy)

    def _quit_app(self):
        """真正退出：保存 + 停托盘 + 销毁（由 mainloop 自然结束，不强制 exit）"""
        if self._save_after_id:
            self.root.after_cancel(self._save_after_id)
            self._save_after_id = None
        self._save()
        if self._tray is not None:
            self._tray.stop()
        self.root.destroy()

    def run(self):
        self.root.mainloop()
