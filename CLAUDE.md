# CLAUDE.md

## 命令

```bash
cd tools/workspace && python main.py                    # 开发运行
pyinstaller --onefile --noconsole --name "待办计时器" --distpath "../" --workpath "../build_tmp" main.py
rm -rf ../build_tmp 待办计时器.spec                      # 打包 → ../待办计时器.exe
```

仅需 Python 3.10+ 标准库，零外部依赖。

## 架构

**数据流**: `用户操作 → MainApp → TodoWidget._fire_change() → MainApp._on_todo_state_change() → _refresh_view() + timer_panel.refresh_all() + _schedule_save()` (300ms 防抖)

**三层分离**: `todo_widget.py` 纯数据模型 (无 tkinter) / `timer_panel.py` 计时 UI (200ms tick 循环) / `main_app.py` + `todo_card.py` 列表 UI (全量重建卡片)

**Tick 机制**: 200ms 间隔遍历面板内所有行刷新，有 running 就持续，全停自动停止。

**顶部吸附**: `none → docked+collapsed (拖到顶≤60px) → docked+expanded (鼠标进入) → docked+collapsed (离开500ms)`。`_check_leave()` 用坐标比对过滤子控件假 Leave。

**运行恢复**: 保存 `running + last_started_at`，加载时补算离线时间 `_base_elapsed += int(time.time() - last_started_at)`。

**数据兼容**: 格式 `{"panel_x": ..., "panel_y": ..., "todos": [...]}`, 旧版纯数组通过 `isinstance(raw, list)` 自动转换。

**原子保存**: `.tmp` → `os.replace()` → 复制 `.bak`。加载损坏时回退 `.bak`。

**日期更新**: 完成待办时 `set_complete()` 自动将 `_date` 设为当天。

## 易错点

- `int(time.time())` 截断亚秒差为 0，测试计时需 sleep ≥1.2s
- 无边框窗口可置屏幕负坐标，吸附依赖此特性
- TimerRow 时间 Label 先于文本 pack (RIGHT 优先)，保时间不被挤出
- Canvas 内嵌 Frame 的 `<Configure>` 事件需手动更新 scrollregion
- 回调链: MainApp 设 `_change_callback` → TimerPanel 覆盖 → 移除时设 None。操作后手动 `_refresh_view()` 是保底机制
