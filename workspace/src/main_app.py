import tkinter as tk
import json
import os
import sys
from datetime import datetime, timedelta

if getattr(sys, 'frozen', False):
    _BASE = os.path.dirname(sys.executable)
else:
    # workspace/src/main_app.py → tools/
    _BASE = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
SAVE_FILE = os.path.join(_BASE, "data", "todos.json")

from .constants import C, FONT
from .utils import flat_btn, get_today_str, fmt_date_cn, fmt_time
from .todo_widget import TodoWidget
from .date_picker import DatePickerDialog
from .todo_card import TodoCard, SectionHeader, EmptyHint


class MainApp:
    def __init__(self):
        self.root = tk.Tk()
        self.root.title("待办计时器")
        self.root.geometry("560x660")
        self.root.configure(bg=C["bg"])
        self.root.minsize(480, 480)

        self.todos: dict[str, TodoWidget] = {}
        self._next_id = 0
        self._next_order = 0
        self.current_date = get_today_str()
        self.filter_state = "active"
        self._cards: list = []  # TodoCard | SectionHeader | EmptyHint
        self._selected_tid: str | None = None

        self._build_ui()
        self._load_todos()
        self._refresh_view()

        self.root.protocol("WM_DELETE_WINDOW", self._on_close)

    # ── UI 构建 ────────────────────────────────────────
    def _build_ui(self):
        # ── 顶栏 ──
        hdr = tk.Frame(self.root, bg=C["surface"], height=48)
        hdr.pack(fill=tk.X)
        hdr.pack_propagate(False)

        tk.Label(hdr, text="待办计时器", bg=C["surface"], fg=C["text"],
                 font=(FONT, 15, "bold")).pack(side=tk.LEFT, padx=20, pady=10)

        self.stat_lbl = tk.Label(hdr, text="", bg=C["surface"], fg=C["text_sec"],
                                 font=(FONT, 10))
        self.stat_lbl.pack(side=tk.RIGHT, padx=20, pady=10)

        tk.Frame(self.root, bg=C["border"], height=1).pack(fill=tk.X)

        # ── 日期导航栏 ──
        date_bar = tk.Frame(self.root, bg=C["surface"])
        date_bar.pack(fill=tk.X)

        date_inner = tk.Frame(date_bar, bg=C["surface"])
        date_inner.pack(fill=tk.X, padx=16, pady=(10, 4))

        self.btn_prev = tk.Label(date_inner, text="‹", bg=C["surface"], fg=C["blue"],
                                 font=(FONT, 20, "bold"), cursor="hand2")
        self.btn_prev.pack(side=tk.LEFT, padx=(0, 8))
        self.btn_prev.bind("<Button-1>", lambda e: self._shift_date(-1))

        self.date_lbl = tk.Label(date_inner, text="", bg=C["surface"], fg=C["text"],
                                 font=(FONT, 14, "bold"))
        self.date_lbl.pack(side=tk.LEFT)

        self.btn_next = tk.Label(date_inner, text="›", bg=C["surface"], fg=C["blue"],
                                 font=(FONT, 20, "bold"), cursor="hand2")
        self.btn_next.pack(side=tk.LEFT, padx=(8, 0))
        self.btn_next.bind("<Button-1>", lambda e: self._shift_date(1))

        flat_btn(date_inner, "今天", self._goto_today,
                 C["blue_light"], C["blue"], font_size=9, pady=3, padx=12,
                 fg=C["blue"]).pack(side=tk.LEFT, padx=(16, 0))

        self.day_total_lbl = tk.Label(date_inner, text="", bg=C["surface"],
                                      fg=C["text_sec"], font=(FONT, 10))
        self.day_total_lbl.pack(side=tk.RIGHT)

        # ── 筛选标签栏 ──
        filter_bar = tk.Frame(self.root, bg=C["surface"])
        filter_bar.pack(fill=tk.X)

        filter_inner = tk.Frame(filter_bar, bg=C["surface"])
        filter_inner.pack(fill=tk.X, padx=16, pady=(0, 8))

        self.filter_chips = {}
        for key, label in [("active", "未完成"), ("completed", "已完成"), ("all", "全部")]:
            chip = tk.Label(
                filter_inner, text=label, bg=C["surface"], fg=C["text_sec"],
                font=(FONT, 10), cursor="hand2", padx=12, pady=3,
            )
            chip.pack(side=tk.LEFT, padx=(0, 4))
            chip.bind("<Button-1>", lambda e, k=key: self._set_filter(k))
            chip.bind("<Enter>", lambda e, c=chip: c.configure(fg=C["blue"]))
            chip.bind("<Leave>", lambda e, c=chip, k=key:
                      c.configure(fg=C["blue"] if self.filter_state == k else C["text_sec"]))
            self.filter_chips[key] = chip

        tk.Frame(self.root, bg=C["border"], height=1).pack(fill=tk.X)

        # ── 输入卡片 ──
        card_inp = tk.Frame(self.root, bg=C["surface"])
        card_inp.pack(fill=tk.X, padx=20, pady=(12, 8))

        card_inp_inner = tk.Frame(card_inp, bg=C["surface"])
        card_inp_inner.pack(fill=tk.X, padx=12, pady=12)

        self.entry = tk.Entry(card_inp_inner, bg=C["bg"], fg=C["text"],
                              insertbackground=C["blue"], relief=tk.SOLID,
                              font=(FONT, 12), bd=1,
                              highlightthickness=0)
        self.entry.pack(side=tk.LEFT, fill=tk.X, expand=True, ipady=8, padx=(0, 10))
        self.entry.bind("<Return>", lambda e: self._add_todo())
        self.entry.focus_set()

        flat_btn(card_inp_inner, "＋ 添加待办", self._add_todo,
                 C["blue"], C["blue_hover"], font_size=12, pady=7, padx=20).pack(
            side=tk.LEFT)

        # ── 列表卡片 ──
        card_list = tk.Frame(self.root, bg=C["surface"])
        card_list.pack(fill=tk.BOTH, expand=True, padx=20, pady=(2, 8))

        lh = tk.Frame(card_list, bg=C["surface"])
        lh.pack(fill=tk.X, padx=16, pady=(12, 4))

        self.list_title_lbl = tk.Label(lh, text="", bg=C["surface"], fg=C["text"],
                                       font=(FONT, 12, "bold"))
        self.list_title_lbl.pack(side=tk.LEFT)

        tk.Label(lh, text="双击展开 · 右键更多", bg=C["surface"], fg=C["text_ter"],
                 font=(FONT, 9)).pack(side=tk.RIGHT)

        tk.Frame(card_list, bg=C["border"], height=1).pack(fill=tk.X, padx=16)

        # ── 可滚动卡片区 ──
        lc = tk.Frame(card_list, bg=C["surface"])
        lc.pack(fill=tk.BOTH, expand=True, padx=4, pady=(4, 12))

        self.canvas = tk.Canvas(lc, bg=C["surface"], highlightthickness=0, bd=0)
        self.scrollbar = tk.Scrollbar(lc, orient=tk.VERTICAL,
                                      command=self.canvas.yview,
                                      bg=C["bg"], troughcolor=C["bg"],
                                      activebackground=C["blue"])
        self.canvas.configure(yscrollcommand=self.scrollbar.set)

        self.card_frame = tk.Frame(self.canvas, bg=C["surface"])
        self.card_frame_id = self.canvas.create_window((0, 0), window=self.card_frame,
                                                       anchor="nw")

        self.canvas.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        self.scrollbar.pack(side=tk.RIGHT, fill=tk.Y)

        # 让 card_frame 宽度跟随 canvas
        self.canvas.bind("<Configure>", self._on_canvas_configure)
        self.card_frame.bind("<Configure>",
                             lambda e: self.canvas.configure(
                                 scrollregion=self.canvas.bbox("all")))

        # 鼠标滚轮
        self.canvas.bind("<Enter>", self._bind_mousewheel)
        self.canvas.bind("<Leave>", self._unbind_mousewheel)

        # ── 底部按钮 ──
        bf = tk.Frame(self.root, bg=C["bg"])
        bf.pack(fill=tk.X, padx=20, pady=(0, 16))

        flat_btn(bf, "▶  全部开始", self._start_all,
                 C["green"], C["green_hover"], font_size=10, pady=6, padx=10).pack(
            side=tk.LEFT, padx=(0, 4))

        flat_btn(bf, "⏸  全部暂停", self._pause_all,
                 C["orange"], C["orange_hover"], font_size=10, pady=6, padx=10).pack(
            side=tk.LEFT, padx=(0, 4))

        flat_btn(bf, "↑  上移", self._move_up_selected,
                 C["text_sec"], C["text_ter"], font_size=10, pady=6, padx=10).pack(
            side=tk.LEFT, padx=(0, 4))

        flat_btn(bf, "↓  下移", self._move_down_selected,
                 C["text_sec"], C["text_ter"], font_size=10, pady=6, padx=10).pack(
            side=tk.LEFT, padx=(0, 4))

        flat_btn(bf, "★  标重点", self._toggle_star_selected,
                 C["orange"], C["orange_hover"], font_size=10, pady=6, padx=10).pack(
            side=tk.LEFT, padx=(0, 4))

        flat_btn(bf, "✓  标记完成", self._toggle_complete_selected,
                 C["green"], C["green_hover"], font_size=10, pady=6, padx=10).pack(
            side=tk.LEFT, padx=(0, 4))

        flat_btn(bf, "✎  编辑", self._edit_selected,
                 C["blue"], C["blue_hover"], font_size=10, pady=6, padx=10).pack(
            side=tk.LEFT, padx=(0, 4))

        flat_btn(bf, "📅  改期", self._change_date,
                 C["text_sec"], C["text_ter"], font_size=10, pady=6, padx=10).pack(
            side=tk.LEFT, padx=(0, 4))

        flat_btn(bf, "🗑  删除", self._delete_selected,
                 C["red"], C["red_hover"], font_size=10, pady=6, padx=10).pack(
            side=tk.LEFT, padx=(0, 4))

    def _on_canvas_configure(self, event):
        self.canvas.itemconfig(self.card_frame_id, width=event.width)

    def _bind_mousewheel(self, event):
        self.canvas.bind_all("<MouseWheel>", self._on_mousewheel)

    def _unbind_mousewheel(self, event):
        self.canvas.unbind_all("<MouseWheel>")

    def _on_mousewheel(self, event):
        self.canvas.yview_scroll(int(-1 * (event.delta / 120)), "units")

    # ── 筛选 ──
    def _set_filter(self, state):
        self.filter_state = state
        for key, chip in self.filter_chips.items():
            if key == state:
                chip.configure(fg=C["blue"], font=(FONT, 10, "bold"))
            else:
                chip.configure(fg=C["text_sec"], font=(FONT, 10))
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
                  order=None, starred=False):
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
                       master_root=self.root, order=w_order, starred=starred)
        if x and y:
            w.win.geometry(f"+{x}+{y}")
        if not visible:
            w.win.withdraw()

        w._date = date or self.current_date

        self.todos[todo_id] = w
        self._refresh_view()
        if hasattr(self, "entry") and text is not None:
            self.entry.delete(0, tk.END)
        self._save()

    def _on_todo_hidden(self, todo_id):
        self._refresh_view()
        self._save()

    def _on_todo_state_change(self):
        self._refresh_view()
        self._save()

    # ── 视图渲染 ────────────────────────────────────────
    def _refresh_view(self):
        self.date_lbl.configure(text=fmt_date_cn(self.current_date))

        for key, chip in self.filter_chips.items():
            if key == self.filter_state:
                chip.configure(fg=C["blue"], font=(FONT, 10, "bold"))
            else:
                chip.configure(fg=C["text_sec"], font=(FONT, 10))

        titles = {"all": "全部待办", "active": "未完成", "completed": "已完成"}
        self.list_title_lbl.configure(text=titles.get(self.filter_state, "待办列表"))

        # 清空卡片
        for child in self.card_frame.winfo_children():
            child.destroy()
        self._cards = []
        self._selected_tid = None

        today_str = self.current_date

        if self.filter_state == "active":
            today_items = []
            cross_items = []
            for tid, w in self.todos.items():
                if w.completed:
                    continue
                w_date = getattr(w, "_date", get_today_str())
                if w_date == today_str:
                    today_items.append(w)
                elif w_date < today_str:
                    cross_items.append(w)

            today_items.sort(key=lambda w: (
                not getattr(w, "starred", False), getattr(w, "_order", 0)))
            cross_items.sort(key=lambda w: (
                not getattr(w, "starred", False), getattr(w, "_order", 0)))

            day_total = 0

            # ── 今日待办 ──
            if today_items:
                hdr = SectionHeader(self.card_frame, "▎今日待办")
                hdr.pack(fill=tk.X)
                self._cards.append(hdr)
            else:
                hint = EmptyHint(self.card_frame, "今日暂无待办")
                hint.pack(fill=tk.X)
                self._cards.append(hint)

            seq = 0
            for w in today_items:
                seq += 1
                day_total += w.elapsed
                card = TodoCard(self.card_frame, w, seq=seq, show_date=False)
                card.callback_click = self._on_card_click
                card.callback_double = self._on_card_double
                card.callback_right = self._on_card_right_click
                card.pack(fill=tk.X)
                self._cards.append(card)

            # ── 跨日未完成 ──
            if cross_items:
                hdr = SectionHeader(self.card_frame,
                                    f"▎跨日未完成 ({len(cross_items)}项)")
                hdr.pack(fill=tk.X)
                self._cards.append(hdr)

                seq = 0
                for w in cross_items:
                    seq += 1
                    card = TodoCard(self.card_frame, w, seq=seq, show_date=True)
                    card.callback_click = self._on_card_click
                    card.callback_double = self._on_card_double
                    card.callback_right = self._on_card_right_click
                    card.pack(fill=tk.X)
                    self._cards.append(card)

            if not today_items and not cross_items:
                hint = EmptyHint(self.card_frame, "暂无待办")
                hint.pack(fill=tk.X)
                self._cards.append(hint)

            active_today = len(today_items)
            self.day_total_lbl.configure(
                text=f"今日合计 {fmt_time(day_total)}  ·  {active_today} 项")

        else:
            day_todos = {tid: w for tid, w in self.todos.items()
                         if getattr(w, "_date", get_today_str()) == today_str}

            if self.filter_state == "completed":
                day_todos = {tid: w for tid, w in day_todos.items() if w.completed}

            sorted_todos = sorted(day_todos.values(), key=lambda w: (
                not getattr(w, "starred", False), getattr(w, "_order", 0)))

            day_total = 0
            seq = 0
            for w in sorted_todos:
                seq += 1
                if not w.completed:
                    day_total += w.elapsed
                card = TodoCard(self.card_frame, w, seq=seq, show_date=False)
                card.callback_click = self._on_card_click
                card.callback_double = self._on_card_double
                card.callback_right = self._on_card_right_click
                card.pack(fill=tk.X)
                self._cards.append(card)

            if not sorted_todos:
                hint = EmptyHint(self.card_frame, "暂无待办")
                hint.pack(fill=tk.X)
                self._cards.append(hint)

            active_count = sum(1 for w in sorted_todos if not w.completed)
            self.day_total_lbl.configure(
                text=f"当日合计 {fmt_time(day_total)}  ·  {active_count} 项")

        # 全局统计
        total = len(self.todos)
        active = sum(1 for w in self.todos.values() if not w.completed)
        done = sum(1 for w in self.todos.values() if w.completed)
        running = sum(1 for w in self.todos.values() if w.running)
        self.stat_lbl.configure(
            text=f"全部 {total} 项 · 未完成 {active} · 已完成 {done} · 运行中 {running}")

    # ── 卡片交互回调 ────────────────────────────────────
    def _on_card_click(self, tid):
        self._select_card(tid)

    def _on_card_double(self, tid):
        if tid in self.todos:
            self.todos[tid].show()
            self._refresh_view()
            self._save()

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
        self._save()

    # ── 操作 ──
    def _toggle_visible(self):
        tid = self._get_selected_id()
        if not tid:
            return
        w = self.todos[tid]
        if w.is_visible():
            w.pause()
            w.win.withdraw()
        else:
            w.show()
        self._refresh_view()
        self._select_card(tid)
        self._save()

    def _toggle_star_selected(self):
        tid = self._get_selected_id()
        if not tid:
            return
        w = self.todos[tid]
        w.starred = not w.starred
        w._build_menu()
        self._refresh_view()
        self._select_card(tid)
        self._save()

    def _toggle_complete_selected(self):
        tid = self._get_selected_id()
        if not tid:
            return
        w = self.todos[tid]
        if w.completed:
            w.set_incomplete()
        else:
            w.set_complete()
        w._build_menu()
        self._refresh_view()
        self._select_card(tid)
        self._save()

    def _edit_selected(self):
        tid = self._get_selected_id()
        if not tid:
            return
        w = self.todos[tid]

        dlg = tk.Toplevel(self.root)
        dlg.title("编辑待办")
        dlg.geometry("420x150")
        dlg.configure(bg=C["surface"])
        dlg.resizable(False, False)
        dlg.transient(self.root)
        dlg.grab_set()

        tk.Label(dlg, text="编辑待办事项", bg=C["surface"], fg=C["text"],
                 font=(FONT, 13, "bold")).pack(pady=(14, 8))

        entry = tk.Entry(dlg, bg=C["bg"], fg=C["text"],
                         insertbackground=C["blue"], relief=tk.FLAT,
                         font=(FONT, 12), bd=0)
        entry.pack(fill=tk.X, padx=20, ipady=7)
        entry.insert(0, w.text)
        entry.select_range(0, tk.END)
        entry.focus_set()

        def save():
            new_text = entry.get().strip()
            if new_text:
                w.set_text(new_text)
                self._refresh_view()
                self._select_card(tid)
                self._save()
            dlg.destroy()

        entry.bind("<Return>", lambda e: save())

        btns = tk.Frame(dlg, bg=C["surface"])
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
            w._date = new_date
            self._refresh_view()
            self._select_card(tid)
            self._save()

        DatePickerDialog(self.root, current, set_date)

    def _delete_selected(self):
        tid = self._get_selected_id()
        if not tid:
            return
        w = self.todos.pop(tid, None)
        if w:
            w.destroy()
        self._selected_tid = None
        self._refresh_view()
        self._save()

    def _start_all(self):
        for card in self._cards:
            if isinstance(card, TodoCard):
                w = card.todo_widget
                if not w.completed:
                    w.start()
        self._refresh_view()

    def _pause_all(self):
        for card in self._cards:
            if isinstance(card, TodoCard):
                card.todo_widget.pause()
        self._refresh_view()

    # ── 持久化 ──────────────────────────────────────────
    def _save(self):
        data = []
        for w in self.todos.values():
            d = w.to_dict()
            d["date"] = getattr(w, "_date", get_today_str())
            data.append(d)
        try:
            os.makedirs(os.path.dirname(SAVE_FILE), exist_ok=True)
            with open(SAVE_FILE, "w", encoding="utf-8") as f:
                json.dump(data, f, ensure_ascii=False, indent=2)
        except Exception:
            pass

    def _load_todos(self):
        if not os.path.exists(SAVE_FILE):
            return
        try:
            with open(SAVE_FILE, "r", encoding="utf-8") as f:
                data = json.load(f)
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
                )
            self._refresh_view()
        except Exception:
            pass

    def _on_close(self):
        self._save()
        self.root.destroy()
        sys.exit(0)

    def run(self):
        self.root.mainloop()
