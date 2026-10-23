import time
from .utils import get_today_str


class TodoWidget:
    """待办计时数据模型 —— 纯状态与计时逻辑，不持有 UI"""

    def __init__(self, todo_id, text, elapsed=0, completed=False,
                 order=0, starred=False, date=None, target_seconds=None,
                 parent_id=None):
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
        self.parent_id = parent_id   # 父任务 id；None=顶层主任务
        self._in_panel = False
        self._callbacks = []  # observer list, 替代单一 _change_callback
        self._version = 0     # 渲染版本号：每次状态变更 +1，UI 层据此跳过无变化重绘
        self.target_seconds = target_seconds  # 目标时长提醒，None=无
        self.reminded = False                 # 是否已触发提醒（防重复）

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
        self.reminded = False  # 计时归零，重新允许提醒
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

    def set_date(self, new_date):
        self._date = new_date
        self._fire_change()

    def set_parent(self, new_parent_id):
        """挂到新父任务下（None=顶层）。合法性(成环/深度)由调用方校验。"""
        self.parent_id = new_parent_id
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
        self._version += 1
        for cb in self._callbacks:
            cb()

    # ── 目标时长提醒 ──

    def set_target(self, seconds):
        """设置目标时长提醒；None 表示清除。重设后重新允许提醒"""
        self.target_seconds = seconds if seconds and seconds > 0 else None
        self.reminded = False
        self._fire_change()

    def clear_target(self):
        if self.target_seconds is not None:
            self.target_seconds = None
            self.reminded = False
            self._fire_change()

    def should_remind(self):
        """是否满足触发条件：有目标、未提醒、运行中、已达标"""
        return (self.target_seconds is not None and not self.reminded
                and self.running and self.get_live_elapsed() >= self.target_seconds)

    def mark_reminded(self):
        self.reminded = True
        self._fire_change()

    # ── 序列化 ──

    def to_dict(self):
        return {
            "id": self.todo_id,
            "text": self.text,
            "parent": self.parent_id,
            "elapsed": self.get_live_elapsed(),
            "completed": self.completed,
            "x": 0,
            "y": 0,
            "visible": self._in_panel,
            "order": self._order,
            "starred": self.starred,
            "running": self.running,
            "last_started_at": self._started_at if self.running else None,
            "target_seconds": self.target_seconds,
            "reminded": self.reminded,
        }
