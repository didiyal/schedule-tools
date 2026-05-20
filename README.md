# 待办计时器 (Todo Timer)

Windows 桌面悬浮待办计时工具 —— 每个待办事项拥有独立的悬浮计时窗口，拖拽管理，数据本地持久化。

## 功能总览

### 桌面悬浮窗
每个待办拥有独立的 **280×125** 无边框悬浮窗，始终置顶，可拖拽移动。
- ▶ 开始 / ⏸ 暂停 / ↺ 重置 独立计时
- 右键菜单：标记完成、标为重点、隐藏悬浮窗
- 标题栏长文字悬停显示完整文本
- 完成状态绿色样式、运行中蓝色样式、重点橙色标识

### 待办管理（主窗口 560×660）
- **日期导航**：‹ › 翻页 + "今天" 快捷跳转，显示当日合计耗时
- **三态筛选**：未完成 / 已完成 / 全部
- **今日待办 + 跨日未完成**：Plan B 双区布局，昨日未完成自动展示
- **跨日按日期分组**：多天未完成各独立分组，子标题显示日期
- **重点标记 ★**：优先处理，重点项优先排序
- **序号排序 ①②③**：↑↓ 上移下移，点击卡片选中后连续操作
- **文字搜索**：实时关键词过滤，300ms 防抖
- **悬停提示**：长文字卡片停留 500ms 显示完整文本

### 数据安全
- **原子保存**：先写 `.tmp` 再 `os.replace`，崩溃不丢数据
- **备份滚动**：自动生成 `.bak` 备份文件
- **运行状态恢复**：重启后自动接续运行中的计时器，补算离线时间
- **加载备份恢复**：主文件损坏时自动从 `.bak` 恢复

### 键盘快捷键

| 按键 | 操作 |
|------|------|
| ↑ ↓ | 导航选中卡片 |
| Space | 标记完成 / 取消完成 |
| Delete | 删除选中（有计时则确认） |
| Ctrl+Z | 撤销上一步操作 |

### 底部按钮
▶ 全部开始 · ⏸ 全部暂停 · ↑ 上移 · ↓ 下移 · ★ 标重点 · ✓ 标记完成 · ✎ 编辑 · 📅 改期 · 🗑 删除

## 技术栈

| 项目 | 说明 |
|------|------|
| 语言 | Python 3.10+ |
| GUI | tkinter |
| 打包 | PyInstaller（单 EXE，约 9 MB） |
| 数据 | 本地 JSON（`data/todos.json`） |

## 项目结构

```
tools/
├── workspace/
│   ├── main.py              # 入口：DPI 感知 → MainApp
│   └── src/
│       ├── __init__.py
│       ├── constants.py     # 飞书风格配色 & 字体 (3370FF 蓝)
│       ├── utils.py         # flat_btn, 时间格式化
│       ├── tooltip.py       # 延迟悬浮提示 (500ms)
│       ├── todo_widget.py   # 桌面悬浮窗口组件
│       ├── todo_card.py     # 列表卡片组件 (TodoCard/SectionHeader/EmptyHint)
│       ├── date_picker.py   # 日期选择对话框
│       └── main_app.py      # 主应用窗口 (869 行)
├── data/
│   └── todos.json           # 持久化数据（自动生成）
├── .gitignore
└── README.md
```

## 模块职责

### `main.py` — 程序入口
启用 Windows Per-monitor DPI 感知，解决高 DPI 屏幕文字模糊问题。

### `constants.py` — 设计系统
飞书风格配色方案：主色 `#3370FF`（蓝）、成功 `#00B578`（绿）、警告 `#FF8F1F`（橙）、危险 `#F53F3F`（红）。统一字体 `Microsoft YaHei`。

### `utils.py` — 工具函数
- `flat_btn(parent, text, cmd, bg, hover_bg, ...)` — 飞书扁平按钮（Frame + Label 组合）
- `fmt_time(s)` — 秒数 → `HH:MM:SS`
- `fmt_date_cn(date_str)` — `2026-05-21` → `2026年5月21日 周四`
- `fmt_date_short(date_str)` — `2026-05-21` → `5月21日`

### `tooltip.py` — 悬浮提示
延迟 500ms 显示，仅对超过 16 字的文本触发。暗色气泡，自动贴边避免超出屏幕。

### `todo_widget.py` — 悬浮待办窗口
每个待办一个 `Toplevel`，`overrideredirect(True)` 无边框，`-topmost` 置顶。
- **真实时钟计时**：`time.time()` 计算增量，200ms 刷新，自动修正 `after()` 漂移
- 标题栏拖拽移动（`<B1-Motion>`）
- 完成/运行/暂停三态样式切换

### `todo_card.py` — 列表卡片
Canvas 内嵌 Frame 列表，替代原生 Listbox。
- `TodoCard`：左侧 3px 色条 + 序号 ①②③ + ★ 重点 + ○●✓ 状态 + 文字 + 时间 + ◌ 隐藏 + 日期
- `SectionHeader`：区块标题（支持 `compact` 紧凑模式）
- `EmptyHint`：空列表占位提示
- `_circled(n)`：1-20 → ①-⑳，21+ → (n)

### `date_picker.py` — 日期选择
`Toplevel` 模态对话框，‹ › 左右翻页 + 确定取消按钮。

### `main_app.py` — 主窗口
核心控制器，管理待办 CRUD、视图渲染、筛选、搜索、排序、持久化。

## 开发

### 运行
```bash
cd tools/workspace
python main.py
```

### 打包
```bash
pip install pyinstaller
pyinstaller --onefile --windowed --name 待办计时器 workspace/main.py
```

### 数据格式
`data/todos.json`：
```json
[
  {
    "id": "1",
    "text": "完成 AI 模型测试",
    "elapsed": 6474,
    "completed": false,
    "x": 200, "y": 150,
    "visible": false,
    "order": 0,
    "starred": true,
    "date": "2026-05-21",
    "running": false,
    "last_started_at": null
  }
]
```

数据路径（相对于 EXE 或项目根目录）：
- 开发：`workspace/src/main_app.py` 向上三层 → `tools/data/todos.json`
- 打包：`sys.executable` 同目录 → `data/todos.json`

## 设计引用

- 配色：飞书 (Feishu) 设计系统
- 字体：Microsoft YaHei（微软雅黑）
- 时间字体：Consolas（等宽数字）
