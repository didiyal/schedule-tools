# 待办计时器 1.4

Windows 桌面待办计时器：主窗口管理每日任务树 + 悬浮计时面板 + 系统托盘 + Agent CLI。

**v1.4 起由 Python/customtkinter 迁移到 C#/.NET 8 + WPF**（动机：渲染性能），
Python 1.2 线已删除（历史见 git 与 `docs/`）。

## 功能

- **每日一棵树**：按日期倒序分组，任务可挂子任务（3 层），折叠/子进度/跨日角标
- **完成态**：绿底灰字删除线，完成不移动卡片；「清除已完成」归档到 archive.json
- **筛选**：未完成 / 已完成 / 只看今日（可叠加，祖先链灰显撑结构）、文字搜索
- **拖拽**：三意图横轴——最左提升带（拖成主任务）、内容左缘（同级，上/下半前后插）、
  偏右（成为子级）；影子跟随显示意图；**跨日期拖放=改期**；今日空槽拖入=改到今天
- **计时面板**：置顶悬浮，▶/⏸/✓/✕ 行内操作，实时时长（含子任务汇总的任务不可入面板）
- **托盘**：关闭=常驻，托盘菜单显示/面板/自启/退出；目标时长提醒（气泡+提示音）
- **CLI**：`TodoTimer.exe cli <命令>` 供 Agent/脚本读写（见下）

## 使用

- 安装包暂未发布；开发运行：`cd wpf && dotnet run`
- 数据在 **EXE 同目录 `data/`**（todos.json + archive.json，便携）
- 快捷键：Tab/Shift+Tab 层级、Delete 删整枝、Ctrl+Z 整枝恢复、Ctrl+Q 退出、双击加面板

## CLI（Agent 接口）

```bash
TodoTimer.exe cli list [--date yyyy-mm-dd | --today] [--json]     # 列表(含 parent/depth)
TodoTimer.exe cli add "任务" [--parent 父id] [--date] [--star]     # 新建(子任务继承父日期)
TodoTimer.exe cli done|undone|start|pause|delete <id> [<id> ...]   # delete=整枝
TodoTimer.exe cli move <id> <yyyy-mm-dd> | rename <id> "文本" | star <id> [--on|--off]
TodoTimer.exe cli reparent <id> --parent 父id | --top
TodoTimer.exe cli search "关键词" | stats
```

GUI 在线时：读命令直读，写命令落盘后经 IPC(127.0.0.1:58372) 通知界面实时刷新；
GUI 离线时直接读写文件。输出 UTF-8，`--json` 供解析。

## 构建

```bash
cd wpf
dotnet build TodoTimer.sln      # 需 .NET 8 SDK
dotnet test  TodoTimer.sln      # 40 项测试
```

目录：`wpf/TodoTimer/`（Models 纯函数+存储 / ViewModels / Views / Services）、
`wpf/TodoTimer.Tests/`（xunit）。安装器（Inno Setup）与发布见 `installer/`（待更新）。

## 文档

- 设计与变更：`docs/`（CHANGELOG、product/<版本>/、ROADMAP.md）
- 历史版本（v1.0–v1.2，Python 实现）见 docs 与 git 历史
