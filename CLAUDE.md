# CLAUDE.md

## 命令

```bash
cd tools/workspace && python main.py                    # 开发运行
pyinstaller --onefile --noconsole --name "待办计时器" --collect-all pystray --collect-all customtkinter --hidden-import six.moves --distpath "../" --workpath "../build_tmp" main.py
rm -rf ../build_tmp 待办计时器.spec                      # 打包 → ../待办计时器.exe
```

Python 3.10+ 标准库 + pystray/pillow（系统托盘）。

## 架构

**数据流**: `用户操作 → MainApp → TodoWidget._fire_change() → _on_todo_state_change() → _refresh_view() + timer_panel.refresh_all() + _ensure_main_tick() + _schedule_save()` (300ms 防抖)

**三层分离**: `todo_widget.py` 纯数据模型 (无 tkinter) / `timer_panel.py` 计时 UI / `main_app.py` + `todo_card.py` 列表 UI (全量重建卡片)

**双 Tick**:
- 计时面板 `timer_panel._tick()` 200ms 刷新面板内所有行。
- 主窗口 `_main_tick()` 200ms 仅更新运行中卡片的时间标签，不重建视图（避免闪烁）。
- 二者都「有 running 就持续，全停自动停止」。

**Observer 回调**: TodoWidget 持有 `_callbacks` 列表（非单一回调）。MainApp 和 TimerPanel 各自通过 `add_callback()` / `remove_callback()` 订阅。`_fire_change()` 遍历全部触发。移除待办时 TimerPanel 用保存的 callback 引用精确移除自己那条。

**三态视图**: active=今日待办 + 跨日未完成分组；completed=所有完成项按日期倒序分组（跨日完成归到完成当天）；all=全部待办按日期倒序分组。

**顶部吸附**: `none → docked+collapsed (拖到顶≤60px) → docked+expanded (鼠标进入) → docked+collapsed (离开500ms)`。`_check_leave()` 用坐标比对过滤子控件假 Leave。

**运行恢复**: 保存 `running + last_started_at`，加载时补算离线时间 `_base_elapsed += int(time.time() - last_started_at)`。

**日期更新**: `set_complete()` 自动将 `_date` 设为当天（跨日完成归到完成日）；`set_date()` 改期。

**归档**: `_archive_completed()` 把所有已完成项移到 `data/archive.json`（带 `archived_at`），从主列表删除。

**系统托盘**: `tray.py` 用 pystray 跑独立线程，菜单回调经 `root.after(0,…)` 投递回主线程。关闭按钮=`_minimize_to_tray`（含面板，刷新存盘）；真正退出走托盘菜单或 `Ctrl+Q` → `_quit_app`（`destroy()` 让 mainloop 自然结束，不 `sys.exit`）。开机自启读写 HKCU `…\Run`。无 pystray 时关闭按钮退化为退出。

**计时提醒**: `target_seconds` 目标时长。`_main_tick` 检查 `should_remind()` → `_fire_reminder`（Beep+托盘通知+模态弹窗），触发后置 `reminded=True` 防重复；`reset()`/`set_target()` 清零允许再触发。

**拖拽排序**: 整卡拖拽+5px 阈值区分点击。`_reorder_in_group` 仅在同 `_date` 组内重排 `_order`（用组内现有 order 升序值重新分配，避免重复值），跨组落点弹回。全局 `ButtonRelease-1` 安全网清理拖拽高亮。

**数据兼容**: 格式 `{"panel_x": ..., "panel_y": ..., "todos": [...]}`, 旧版纯数组通过 `isinstance(raw, list)` 自动转换。

**原子保存**: `.tmp` → `os.replace()` → 复制 `.bak`。加载损坏时回退 `.bak`。

## 易错点

- `int(time.time())` 截断亚秒差为 0，测试计时需 sleep ≥1.2s
- 无边框窗口可置屏幕负坐标，吸附依赖此特性
- TimerRow 时间 Label 先于文本 pack (RIGHT 优先)，保时间不被挤出
- Canvas 内嵌 Frame 的 `<Configure>` 事件需手动更新 scrollregion
- Observer 回调用列表存引用，TimerPanel 必须用 `remove_callback()` 精确移除自己的那条，否则内存泄漏
