# AGENTS.md — 待办计时器 (scheduleTools/tools)

Windows 桌面待办计时器：主窗口管理待办列表 + 悬浮计时面板 + 系统托盘。
Python 3.10+ 标准库 + customtkinter/pystray/pillow，PyInstaller 打包单 EXE，
Inno Setup 打安装包。深度参考 **CLAUDE.md**（架构细节/易错点的权威来源），
本文件只留工作要点。

## 目录

- `workspace/main.py` → 入口；`workspace/src/` 全部源码（main_app 控制器、
  timer_panel 悬浮面板、todo_widget 纯数据模型、todo_card 卡片、tray 托盘等）
- `data/` — **用户运行数据（todos.json，持续在用），严禁改动/提交/删除**；
  测试一律复制到临时目录并改 `main_app.SAVE_FILE/ARCHIVE_FILE`，
  同时 mock `set_autostart`（会写注册表）与 `TrayController.start/pump`
- `installer/` — Inno Setup 脚本 + 一键打包 bat + 图标生成
- `docs/` — CHANGELOG、product/<版本>/changelog、ROADMAP、测试策略

## 常用命令

```bash
cd workspace && python main.py                 # 开发运行
installer/build_installer.bat                  # 一键: 图标→PyInstaller→iscc (GBK 编码, 用 cmd 跑)
"C:\Program Files (x86)\Inno Setup 6/ISCC.exe" installer/installer.iss   # 只重编安装器
```

无自动化测试套件；验证手段见 `docs/development/v1.0/testing-strategy.md`。


### CLI / Agent 接口

供 AI Agent 读写待办数据（与 GUI 实时同步；GUI 未运行时直读写 `data/todos.json`）：

```bash
cd workspace
python todo_cli.py list [--date YYYY-MM-DD | --all] [--json]   # 今日任务/耗时/状态
python todo_cli.py add "任务" [--date YYYY-MM-DD] [--star]
python todo_cli.py done <id> | undo <id> | delete <id> | start <id> | pause <id>
python todo_cli.py move <id> YYYY-MM-DD | rename <id> "新文本" | star <id> [--on|--off]
```

打包版等价：`待办计时器.exe cli list --json`。写命令 GUI 在线走 IPC(即时上屏)，GUI 运行中但 IPC 不可达时拒绝写文件(防覆盖)。`--json` 供 Agent 解析。
## 必守规则

- **版本号两处同步**：`src/constants.py` 的 `APP_VERSION` ↔ `installer/installer.iss`
  的 `MyAppVersion`（安装包文件名由 iss 派生）。
- **文档同步纪律**（CLAUDE.md 末尾）：用户可见变更 → `docs/CHANGELOG.md` 摘要 +
  `docs/product/<版本>/changelog.md` 详情；架构/约束变化 → 更新 CLAUDE.md。
- **编码**：`installer.iss` 与 `ChineseSimplified.isl` 必须 UTF-8 **带 BOM**；
  `build_installer.bat` 是 GBK+CRLF（中文 Windows cmd 才能解析中文 EXE 名），
  改动时按字节处理，勿用默认 UTF-8 重写。

## 架构要点（改 UI 前必读）

- **数据流**：变更 → `TodoWidget._fire_change()`（递增 `_version`）→ observer
  回调列表（MainApp 与 TimerPanel 各订阅）→ 刷新 + 300ms 防抖存盘。
- **渲染管线（v1.1.1）**：`_refresh_view` 先纯计算渲染计划，计划相同整体跳过；
  卡片序列不变走同值守卫逐卡同步，纯增/纯删走原地增删，切视图/排序才全量
  重排（首屏同步 + 剩余分帧流式补齐）。选中状态跨刷新保留。
- **双 Tick**：主窗口与计时面板各 200ms，仅刷新时间标签，有 running 才持续。
- **托盘线程**：pystray 回调经 queue + 主线程 80ms pump 投递，不能直接
  `root.after`；关闭按钮=最小化到托盘，真退出走托盘菜单或 Ctrl+Q。
- **持久化**：`.tmp` 原子写 → `os.replace` → `.bak` 滚动备份；旧数组格式兼容。

## 易错点（踩过的坑）

- CTk 控件 **configure 同值也整控件重绘**；高频路径必须同值守卫。
- Canvas 窗口 item 内的容器联动 `scrollregion` 会把批量重排放大 ~7 倍——
  已改 50ms 合并，勿改回直连。
- CTk 卡片**首次映射**远贵于重映射；预热须真实映射一次（place 可视区外）。
- 卡片仅外壳是 CTk；内部纯 tk 子控件 bg 不透明，换底色必须走
  `TodoCard._apply_fg` 同步。
- `int(time.time())` 截断亚秒：测计时逻辑 sleep ≥ 1.2s。
- **UI 层 bug 在逻辑层复现不出来**（如"切视图清空选中→✓ 静默无操作"）；
  疑似 UI bug 先用 GUI 自动化走真实点击路径。
- Git Bash 调 ISCC 用正斜杠路径；给 Setup/unins000 传 `/SWITCH` 参数须经
  `cmd //c`（MSYS 会吞斜杠参数）。
