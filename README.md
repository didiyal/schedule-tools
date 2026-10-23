# 待办计时器 (Todo Timer)

Windows 桌面悬浮待办计时工具 —— 主窗口管理待办列表，统一计时面板显示所有运行中待办的实时计时。支持系统托盘、计时提醒、拖拽排序。数据本地持久化。

## 功能总览

### 统一计时面板

所有计时待办整合到一个悬浮面板中，替代旧版每个待办独立窗口的方案。

- ▶ 开始 / ⏸ 暂停 / ↺ 重置 / ✕ 移除 独立控制
- 实时显示运行状态 ● / ○ 和计时 `HH:MM:SS`
- 右键菜单：标记完成、标为重点、开始/暂停/重置、移除
- **顶部吸附**：拖到屏幕顶部自动吸顶收起为标签条，鼠标移入展开，移出自动收回
- 面板位置、展开/收起状态持久化
- 关闭面板后双击待办卡片即可重新打开

### 待办管理（主窗口 560×660）
- **日期导航**：‹ › 翻页 + "今天" 快捷跳转
- **筛选勾选**：默认显示全部；勾选「未完成」/「已完成」缩小范围，可叠加（双勾 = 全部）
- **任务树**：任务可挂子任务（最多 3 层），右键「添加子任务」、Tab/Shift+Tab 降级/提升、拖拽三区落点（上/下 = 同父兄弟插入，中间 = 成为子任务）；父卡片显示子进度 `n/m` 与折叠箭头
- **每日一棵树**：按日期倒序分组，子任务永远挂在父任务下（跨日显示日期角标），完成与未完成同树直观可见
- **完成态变绿**：完成的任务绿底灰字 + `[已完成]`，位置不移动
- **分页加载**：每页 30 张卡片，尾部「加载更多」+ 滚动到底自动追加，大列表不卡顿
- **重点标记 ★**：优先处理，重点项优先排序
- **拖拽排序**：同父兄弟内重排 + 拖到卡片中部改为其子任务，成环/超层自动弹回（↑↓ 按钮仍可精确操作）
- **计时提醒 ⏰**：为待办设置目标时长（5/15/25/60 分钟预设或自定义），达成时弹窗+声音+托盘通知，卡片实时显示剩余时间
- **序号排序**：↑↓ 上移下移（兄弟列表内），点击卡片选中后连续操作
- **文字搜索**：实时关键词过滤，300ms 防抖
- **悬停提示**：长文字卡片停留 500ms 显示完整文本

### 系统托盘
- **最小化到托盘**：关闭按钮隐藏到托盘而非退出（同时隐藏计时面板，刷新存盘）
- **托盘菜单**：显示窗口 / 开机自启（勾选）/ 退出；双击图标恢复窗口
- **开机自启**：勾选后写入注册表，开机自动启动
- **真正退出**：托盘菜单"退出"或快捷键 Ctrl+Q

### 数据安全
- **原子保存**：先写 `.tmp` 再 `os.replace`，崩溃不丢数据
- **备份滚动**：自动生成 `.bak` 备份文件
- **运行状态恢复**：重启后自动接续运行中的计时器，补算离线时间
- **加载故障回退**：主文件损坏时自动从 `.bak` 恢复
- **旧格式兼容**：自动检测并转换旧版数组格式数据
- **归档**：🗑 清除已完成按钮把完成项移到 `data/archive.json`，保持主列表清爽

### CLI / Agent 接口

供 AI Agent 与脚本读写待办数据，与 GUI 实时同步：

```bash
cd tools/workspace
python todo_cli.py list [--date YYYY-MM-DD | --all | --from A --to B] [--search 关键词] [--tree] [--json]
python todo_cli.py search 关键词 [--json]              # 跨全部日期搜索
python todo_cli.py stats [--date ... | --all] [--top N] [--json]   # 耗时/数量统计
python todo_cli.py add "任务文本" [--date YYYY-MM-DD] [--star] [--parent 父id]
python todo_cli.py done <id> [id...] | undo <id> [id...] | delete <id> [id...]  # 批量; delete 连同子任务整枝删除
python todo_cli.py move <id> YYYY-MM-DD | rename <id> "新文本" | star <id>
python todo_cli.py reparent <id> (--parent 父id | --top)  # 移动整枝（成环/超3层拒绝）
python todo_cli.py start <id> | pause <id>
```

`list` 输出含 `parent` / `depth` 字段，按 DFS 森林序排列（与界面一致）；
`--tree` 树形缩进显示。GUI 运行中走本地 IPC（改动立即显示在界面）；未运行时
直接读写 `data/todos.json`。打包版：`待办计时器.exe cli list --tree --json`。
### 键盘快捷键

| 按键 | 操作 |
|------|------|
| ↑ ↓ | 导航选中卡片 |
| Space | 标记完成 / 取消完成 |
| Tab / Shift+Tab | 降为子任务 / 提升层级 |
| Delete | 删除选中（连同子任务，有计时则确认） |
| Ctrl+Z | 撤销上一步操作（删整枝可整枝恢复） |

### 底部按钮
- 第一行：▶ 全部开始 · ⏸ 全部暂停 · ★ 标重点 · ✓ 标记完成 · ✎ 编辑 · 📅 改期
- 第二行：↑ 上移 · ↓ 下移 · 🗑 删除 · 🗄 清除已完成（归档到 archive.json）

## 技术栈

| 项目 | 说明 |
|------|------|
| 语言 | Python 3.10+ |
| GUI | tkinter |
| 托盘 | pystray + pillow |
| 打包 | PyInstaller（单 EXE，约 12 MB） |
| 数据 | 本地 JSON（`data/todos.json`） |
| 依赖 | 标准库 + pystray/pillow |

## 项目结构

```
tools/
├── workspace/
│   ├── main.py              # 入口：DPI 感知 → MainApp.run()；`main.py cli` → 命令行
│   ├── todo_cli.py          # CLI 客户端（argparse + IPC/降级直读写）
│   └── src/
│       ├── main_app.py      # 主窗口控制器 — CRUD/树渲染/筛选/分页/搜索/持久化
│       ├── timer_panel.py   # 统一计时悬浮面板 — 顶部吸附、200ms tick
│       ├── todo_widget.py   # 待办数据模型 — 纯状态+计时逻辑+parent，不持有 UI
│       ├── tree.py          # 任务树纯函数（parent 映射，GUI/headless 共用）
│       ├── todo_card.py     # 列表卡片 UI — TodoCard/SectionHeader/EmptyHint/LoadMoreHint
│       ├── tray.py          # 系统托盘 + 开机自启（pystray）
│       ├── cli_server.py    # 本地 CLI/Agent 服务（GUI 内嵌，127.0.0.1 TCP）
│       ├── cli_headless.py  # GUI 未运行时 CLI 的直接读写路径
│       ├── constants.py     # 飞书风格配色 & 字体常量
│       ├── utils.py         # flat_btn、时间/日期格式化
│       ├── tooltip.py       # 延迟悬浮提示 (500ms)
│       └── date_picker.py   # 模态日期选择对话框
├── data/
│   ├── todos.json           # 运行时自动生成
│   └── archive.json         # 已归档的已完成待办（清除已完成时生成）
├── CLAUDE.md                # Claude Code 项目指南
└── README.md
```

## 模块职责

### `main.py` — 程序入口
启用 Windows Per-monitor DPI 感知，创建 MainApp 并进入主循环。

### `main_app.py` — 主窗口控制器
核心控制器：待办 CRUD、视图渲染、筛选/搜索、排序、键盘快捷键、持久化读写。

### `timer_panel.py` — 统一计时面板
单例 Toplevel 悬浮窗：Canvas+Scrollbar 可滚动列表、200ms 全局 tick、顶部吸附+自动收回。

### `todo_widget.py` — 待办数据模型
纯数据模型，不持有 UI。真实时钟计时（`time.time()`），变更回调通知上层。

### `todo_card.py` — 列表卡片
Canvas 内嵌 Frame 卡片列表（非 Listbox）：
- `TodoCard`：3px 色条 + 序号 + ★ + 状态 + 文字 + 时间 + 日期
- `SectionHeader`：分区分组标题
- `EmptyHint`：空列表占位

### `constants.py` — 设计系统
飞书风格配色（蓝 `#3370FF`、绿 `#00B578`、橙 `#FF8F1F`、红 `#F53F3F`），统一字体 `Microsoft YaHei`。

### `utils.py` — 工具函数
`flat_btn()` 飞书扁平按钮、`fmt_time()` / `fmt_date_cn()` 格式化。

### `tooltip.py` — 悬浮提示
500ms 延迟显示，仅超 16 字触发，暗色气泡自动贴边。

### `date_picker.py` — 日期选择
模态对话框，‹ › 翻页 + 确定取消。

## 开发

### 运行
```bash
cd tools/workspace
python main.py
```

### 打包
```bash
cd tools/workspace
pyinstaller --onefile --noconsole --name "待办计时器" --collect-all pystray --hidden-import six.moves --distpath "../" --workpath "../build_tmp" main.py
rm -rf ../build_tmp 待办计时器.spec
```

产物：`tools/待办计时器.exe`（约 12 MB）。`--collect-all pystray` 确保打包后托盘后端可用。

### 数据格式
`data/todos.json`：
```json
{
  "panel_x": 1492,
  "panel_y": 0,
  "collapsed": [],
  "todos": [
    {
      "id": "1",
      "text": "完成 AI 模型测试",
      "parent": null,
      "elapsed": 6474,
      "completed": false,
      "x": 0, "y": 0,
      "visible": false,
      "order": 0,
      "starred": true,
      "date": "2026-05-21",
      "running": false,
      "last_started_at": null
    }
  ]
}
```

- `parent` = 父任务 id（`null` = 顶层主任务；v1.2 纯增量字段，旧版本自动忽略）
- `collapsed` = 折叠的任务 id 列表（树展开状态持久化）
- `visible` = `true` 表示该待办在计时面板中
- `running` + `last_started_at` 用于运行状态恢复
- `date` 在 `set_complete()` 时自动更新为完成当天
- 旧版数组格式自动兼容

归档文件 `data/archive.json`：纯数组，每项比上述多一个 `archived_at`（归档日期）。由「🗄 清除已完成」按钮生成，仅追加写入，不参与主列表加载。

数据路径解析：
- 开发：`workspace/src/main_app.py` 向上三层 → `tools/data/todos.json`
- 打包：`sys.executable` 同目录 → `data/todos.json`

## 设计引用

- 配色：飞书 (Feishu) 设计系统
- 字体：Microsoft YaHei（微软雅黑）
- 时间字体：Consolas（等宽数字）
- 吸附交互：参考 QQ 悬浮窗
