# 开发任务拆解 — v1.0

按模块组织，对应 `workspace/src` 下的源码。

## M-1 数据模型 `todo_widget.py`

- 待办状态：`text` / `elapsed` / `completed` / `starred` / `date` / `order` / `visible` / `running` / `last_started_at` / `target_seconds` / `reminded`。
- 真实时钟计时：基于 `time.time()`，`get_live_elapsed()` 实时累计。
- Observer 回调：`_callbacks` 列表，`add_callback` / `remove_callback`，`_fire_change()` 遍历触发。
- 生命周期：`set_complete()` 自动把 `_date` 设为完成当天；`set_date()` 改期；`reset()` / `set_target()` 清 `reminded` 允许再次提醒。
- 序列化：`to_dict()` / `from_dict()`；旧数组格式兼容。

## M-2 主窗口控制器 `main_app.py`

- CRUD 与全量重建卡片（`_refresh_view`）。
- 三态视图分组：active / completed / all。
- 筛选与 300ms 防抖搜索。
- 排序：拖拽 `_reorder_in_group`（同 `date` 组内重排 `_order`）、序号 ↑ / ↓。
- 持久化：300ms 防抖 `_schedule_save`；原子保存（`.tmp` → `os.replace` → `.bak`）。
- 加载：损坏回退 `.bak`；旧数组格式转换；运行恢复补算 `_base_elapsed += int(time.time() - last_started_at)`。
- 归档：`_archive_completed()` 写 `archive.json`（带 `archived_at`）。
- 双 Tick：`_main_tick()` 200ms 仅更新运行中卡片时间标签并检查 `should_remind()`。
- 滚轮：`bind_all` + `_toplevel_of()` 顶层窗口坐标守卫，不串扰计时面板。

## M-3 计时面板 `timer_panel.py`

- 单例 `Toplevel` 悬浮窗，Canvas + Scrollbar 可滚动行列表。
- `_tick()` 200ms 刷新所有行。
- 顶部吸附状态机：`none → docked+collapsed → docked+expanded → docked+collapsed`。
- `_check_leave()` 用坐标比对过滤子控件假 Leave。
- 关闭后双击重开（`deiconify`）。
- 滚轮：`bind_all` + 本面板坐标守卫，不串扰主窗口。
- 各自 `add_callback` / `remove_callback` 订阅状态变更（用保存的 callback 引用精确移除）。

## M-4 列表卡片 `todo_card.py`

- `TodoCard`：3px 色条 + 序号 + ★ + 状态 + 文字 + 时间 + 日期。
- `SectionHeader` 分组标题、`EmptyHint` 空提示。
- 整卡拖拽（`Button-1` / `B1-Motion` / `ButtonRelease-1`），5px 阈值区分点击。
- 选中 / 悬停 / 拖拽视觉反馈（`set_selected` / `set_dragging`）。
- `update_reminder()` 刷新 ⏰ 倒计时。

## M-5 系统托盘 `tray.py`

- pystray 独立线程，菜单回调经 `queue` + `root.after(0, …)` 投递回主线程。
- 动态菜单标签（开机自启勾选态），`refresh_menu()` 调 `update_menu()`。
- `autostart_enabled` / `set_autostart` 读写 HKCU `Run`。
- 关闭按钮 = `_minimize_to_tray`；退出 = `_quit_app`（`destroy()` 让 mainloop 自然结束，不 `sys.exit`）。
- 无 pystray 时关闭退化为退出。

## M-6 入口与常量

- `main.py`：Windows Per-monitor DPI 感知 → `MainApp.run()`。
- `constants.py`：飞书风格配色 + Microsoft YaHei 字体。
- `utils.py`：`flat_btn` / `fmt_time` / `fmt_date`。
- `tooltip.py`：500ms 延迟悬浮提示。
- `date_picker.py`：模态日期选择对话框。
