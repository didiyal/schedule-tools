# 待办计时器

任务树 + 计时器：Windows 桌面端（WPF）+ 安卓端（Flutter），经自建服务端三端同步。
**本地优先**——桌面端离线一切可用，联网后自动双向同步；服务端按空间多租户隔离。

- **v1.4**（2026-10-26）：Python/customtkinter → C#/.NET 8 + WPF 全量重写（动机：渲染性能）
- **v1.5**（2026-10-27）：架构分离——TodoSync 服务端 + 桌面同步模块 + 安卓 APP；
  每日顺延（跨日自动结转）+ 补卡语义 + 服务端空间隔离；首个安装包发布

## 功能

### 桌面端（wpf/，主入口）

- **每日一棵树**：按日期倒序分组，任务可挂子任务（3 层），折叠/子进度/跨日角标
- **完成态**：绿底灰字删除线，完成不移动卡片；「清除已完成」归档到 archive.json
- **筛选**：未完成 / 已完成 / 只看今日（可叠加，祖先链灰显撑结构）、文字搜索
- **拖拽**：三意图横轴——最左提升带（拖成主任务）、内容左缘（同级，上/下半前后插）、
  偏右（成为子级）；影子跟随显示意图；**跨日期拖放=改期**；今日空槽拖入=改到今天
- **计时面板**：置顶悬浮，▶/⏸/✓/✕ 行内操作，实时时长
- **父子规则**：首次建子任务时父任务时长/面板身份自动迁移给同名子任务；
  含子任务的任务时长=子任务递归汇总，不可单独计时/入面板；父任务卡三档视觉区分
- **云同步**（v1.5）：配置服务器+令牌后自动推拉（存盘即推、30s 心跳拉），
  LWW 合并、删除墓碑、失败重试；未配置零联网，本地功能不受影响
- **每日顺延**（v1.5）：跨日自动结转——旧日没完成的任务搬到今天，
  有进度的旧树留「已结转」灰字快照（不占完成语义）；**补卡语义**——
  完成不强制改期，昨天忘勾的任务今天补勾仍留在昨天的历史组
- **托盘**：关闭=常驻，托盘菜单显示/面板/自启/退出；目标时长提醒（气泡+提示音）
- **CLI**：`TodoTimer.exe cli <命令>` 供 Agent/脚本读写（见下）

### 安卓端（app/，v1.5 新增）

任务树浏览（日组+3 层）、勾选、新建任务/子任务、删除（全端同步删除）、
开始/暂停计时、只看今日、下拉刷新；30s 轮询+回前台即同步；TLS 自签证书钉扎。
构建：`cd app && bash build_apk.sh release`（详见 `app/README.md`）。

### 服务端（server/TodoSync/，v1.5 新增）

ASP.NET Core 8 + SQLite 同步枢纽：设备令牌鉴权、配对码、增量 API（`/api/changes?since=N`）、
墓碑、**空间（space）多租户隔离**——令牌绑定空间，数据互相不可见；配对码可指定空间
（共享清单「一空间多用户」留待后续）。已部署（systemd + Nginx 443 自签反代），
运维手册见 `server/TodoSync/README.md`。

## 三端同步语义

- 按条目 `updated_at` 秒级 **last-writer-wins**；删除走墓碑（防删后复活）
- 桌面端=权威端（管理员令牌）；手机经配对码/令牌接入
- **空间隔离**：每用户/每空间独立数据面，互不可见；令牌与空间一一绑定

## 使用（桌面端）

- 安装包：**[GitHub Releases](https://github.com/didiyal/schedule-tools/releases)**
  下载 `待办计时器_Setup_1.4.0.exe`（self-contained 零依赖，按用户安装免管理员，
  原地升级自动继承数据）；开发运行：`cd wpf && dotnet run`
- 数据在 **EXE 同目录 `data/`**（todos.json + archive.json + syncstate.json，便携）
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
cd wpf && dotnet build TodoTimer.sln && dotnet test TodoTimer.sln   # 桌面端，64 项测试
cd app && bash build_apk.sh release                                 # 安卓端 APK
cd server/TodoSync && dotnet publish -c Release -r linux-x64 ...    # 服务端（见其 README）
```

目录：`wpf/TodoTimer/`（Models / ViewModels / Views / Services/Themes）、
`wpf/TodoTimer.Tests/`（xunit）、`app/`（Flutter）、`server/TodoSync/`（同步枢纽）、
`installer/`（Inno Setup）、`docs/`。

## 文档

- 设计与变更：`docs/`（CHANGELOG、product/<版本>/、ROADMAP.md）
- v1.5 架构决策：`docs/product/v1.5/design-server-app.md`
- 部署运维：`server/TodoSync/README.md`；安卓构建：`app/README.md`
- 历史版本（v1.0–v1.2，Python 实现）见 docs 与 git 历史
