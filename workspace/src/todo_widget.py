import time
from .utils import get_today_str


class TodoWidget:
    """待办计时数据模型 —— 纯状态与计时逻辑，不持有 UI"""

    def __init__(self, todo_id, text, elapsed=0, completed=False,
                 order=0, starred=False, date=None):
        self.todo_id = todo_id
        self.text = text
        self.elapsed = elapsed
        self._base_elapsed = elapsed
        self._started_at = None
        self.running = False
        self.completed = completed
        self._order = order
        self.starred = starred
        self._date = date
        self._in_panel = False
        self._callbacks = []  # observer list, 替代单一 _change_callback

    # ── 计时 ──

    def get_live_elapsed(self):
        if self.running:
            return self._base_elapsed + int(time.time() - self._started_at)
        return self.elapsed

    def start(self):
        if self.completed or self.running:
            return
        self.running = True
        self._started_at = time.time()
        self._base_elapsed = self.elapsed
        self._fire_change()

    def pause(self):
        if not self.running:
            return
        self.running = False
        self.elapsed = self._base_elapsed + int(time.time() - self._started_at)
        self._fire_change()

    def reset(self):
        if self.completed:
            return
        was_running = self.running
        self.pause()
        self.elapsed = 0
        self._base_elapsed = 0
        if was_running:
            self.start()

    # ── 状态变更 ──

    def set_complete(self):
        self.pause()
        self.completed = True
        self._date = get_today_str()
        self._fire_change()

    def set_incomplete(self):
        self.completed = False
        self._fire_change()

    def set_text(self, new_text):
        self.text = new_text
        self._fire_change()

    # ── 面板可见性 ──

    def is_visible(self):
        return self._in_panel

    def show(self):
        self._in_panel = True

    def hide(self):
        self.pause()
        self._in_panel = False

    def destroy(self):
        self.pause()

    # ── 回调 (observer list) ──

    def add_callback(self, cb):
        """添加状态变更回调，同一 callable 不会重复添加"""
        if cb not in self._callbacks:
            self._callbacks.append(cb)

    def remove_callback(self, cb):
        """移除回调"""
        if cb in self._callbacks:
            self._callbacks.remove(cb)

    def _fire_change(self):
        for cb in self._callbacks:
            cb()

    # ── 序列化 ──

    def to_dict(self):
        return {
            "id": self.todo_id,
            "text": self.text,
            "elapsed": self.get_live_elapsed(),
            "completed": self.completed,
            "x": 0,
            "y": 0,
            "visible": self._in_panel,
            "order": self._order,
            "starred": self.starred,
            "running": self.running,
            "last_started_at": self._started_at if self.running else None,
        }
