# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 常用命令

```bash
# 开发运行
cd tools/workspace && python main.py

# 打包为单文件 EXE（产物输出到 tools/待办计时器.exe）
cd tools/workspace
pyinstaller --onefile --noconsole --name "待办计时器" --distpath "../" --workpath "../build_tmp" main.py
rm -rf ../build_tmp 待办计时器.spec
```

项目零外部依赖，仅需 Python 3.10+ 标准库（tkinter、json、time、datetime、ctypes）。

## 架构核心

### 数据流闭环

```
用户操作 → MainApp → TodoWidget._fire_change()
  → MainApp._on_todo_state_change()
    → _refresh_view()           # 重建主窗口卡片列表（Canvas+Frame）
    → timer_panel.refresh_all() # 刷新计时面板所有行的时间显示
    → _schedule_save()          # 300ms 防抖后原子写入
```

`_fire_change()` 是整个系统的中枢神经——所有状态变更（开始/暂停/重置/完成/编辑）最终都通过这一个回调汇聚到 MainApp，触发视图刷新和持久化。`_change_callback` 由 MainApp 在 `_add_todo()` 时注入到每个 TodoWidget 实例。

### 三层职责分离

| 层 | 文件 | 关键点 |
|---|---|---|
| 数据模型 | `todo_widget.py` | 纯计算，无 tkinter 导入。`get_live_elapsed()` 基于 `time.time()` 实时计算，消除 `after()` 漂移 |
| 计时 UI | `timer_panel.py` | 单例 Toplevel，一个全局 200ms `after()` 循环驱动所有 TimerRow 刷新。每行持有对 TodoWidget 的引用，只读取不写入 |
| 列表 UI | `todo_card.py` + `main_app.py` | main_app 是唯一控制器，_refresh_view() 全量重建卡片（非增量更新） |

### TimerPanel 的 Tick 机制

`TimerPanel._tick()` 以 200ms 间隔运行。它遍历 `self._rows` 中的每一行，调用 `row.refresh()`，后者从 `row.todo_widget.get_live_elapsed()` 读取实时时间并更新 Label。只要至少有一个 TodoWidget 处于 `running` 状态，tick 就持续；所有都暂停时自动停止。

### 顶部吸附状态机

```
none → (拖到顶部≤60px释放) → docked+collapsed
docked+collapsed → (鼠标进入) → docked+expanded
docked+expanded → (鼠标离开500ms) → docked+collapsed
docked+* → (往下拖>80px) → none
```

子控件间鼠标移动会触发 Toplevel 的 `<Leave>` 事件，`_check_leave()` 通过比对 `winfo_pointerxy()` 与窗口实际边界来过滤假离开。

### 运行状态恢复

保存时记录 `running` 和 `last_started_at`（Unix 时间戳）。加载时若 `running=True` 且 `last_started_at` 不为 None，则计算 `_base_elapsed += int(time.time() - last_started_at)` 补算离线时间，然后重新 `start()`。这使得应用关闭/重启对计时完全透明。

## 数据格式与兼容

`data/todos.json` 当前格式：

```json
{"panel_x": 1492, "panel_y": 0, "todos": [...]}
```

旧版为纯数组 `[{...}]`。`_load_todos()` 通过 `isinstance(raw, list)` 检测并自动转换。保存路径在开发时从 `workspace/src/main_app.py` 向上三级到 `tools/`，打包后取 `sys.executable` 同目录。

原子写入策略：先写 `.tmp` → `os.replace()` 替换正式文件 → 复制 `.bak` 备份。加载时主文件损坏自动回退 `.bak`。

## 易错点

- **`int(time.time())` 截断**：`get_live_elapsed()` 的 `int()` 会将亚秒级时间差截断为 0。测试计时功能时至少 sleep 1.2 秒，否则 `elapsed` 恒为 0。
- **`overrideredirect` 窗口定位**：无边框窗口可被置于屏幕负坐标区域，吸附功能依赖此特性将面板主体藏于屏幕上方。
- **pack 顺序决定可见性**：TimerRow 中时间 Label 先于文本 Label pack（`side=RIGHT` 优先分配空间），确保长文本不会把时间挤出视野。
- **Canvas 内嵌 Frame**：`_refresh_view()` 和 TimerPanel 都使用 `create_window` 在 Canvas 内嵌入 Frame 实现滚动。Frame 的 `<Configure>` 事件需手动更新 `scrollregion`。
