import time


class TodoWidget:
    """待办计时数据模型 —— 纯状态与计时逻辑，不持有 UI"""

    def __init__(self, todo_id, text, elapsed=0, completed=False,
                 order=0, starred=False):
        self.todo_id = todo_id
        self.text = text
        self.elapsed = elapsed
        self._base_elapsed = elapsed  # 暂停时的累计秒数
        self._started_at = None       # 启动时的 time.time()
        self.running = False
        self.completed = completed
        self._order = order
        self.starred = starred
        self._date = None             # 由 MainApp 设置
        self._in_panel = False        # 是否在统一计时面板中
        self._change_callback = None  # 状态变更回调

    # ── 计时（基于真实时钟，避免累积漂移）──

    def get_live_elapsed(self):
        """实时耗时：运行中动态计算，暂停中返回已结算值"""
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

    # ── 回调 ──

    def _fire_change(self):
        if self._change_callback:
            self._change_callback()

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
