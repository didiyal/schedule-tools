# 待办计时器 v1.4.0 设计（C#/WPF 重写）

> 状态：待评审。
> 拍板记录：2026-10-23 用户定 —— 动机=渲染性能，选成熟路线 **C# + WPF**；
> 现有功能整体重写为 **v1.4.0**；拖拽「成为子任务」bug 留到新版修复
> （v1.2.0 changelog 已知问题）。

## 0. 结论速览

- **动机与选型**：v1.1–v1.2 在 customtkinter 上堆的同值守卫/预热/分帧/防抖
  补丁是技术栈固有成本。WPF 是保留模式渲染 + UI 虚拟化，这一类问题在框架层
  不存在，选成熟路线 .NET 8 (LTS) + WPF。
- **范围**：与 v1.2.0 功能对等 + 拖拽「成为子任务」bug 修复。
  Python 线（`workspace/`）**冻结于 v1.2.0**，不再演进，仅作参考实现。
- **契约不变**（重写不迁移用户数据、不废已有脚本）：
  `data/todos.json|archive.json` 格式与位置、CLI 命令面与 `--json`、
  IPC 127.0.0.1:58372 协议、Inno Setup 安装器（同 AppId 覆盖升级）。
- **前置条件**：本机无 .NET SDK（也无 winget）→ 用官方 dotnet-install 脚本
  装便携 SDK 到固定目录（免管理员、不写系统目录、可整目录删除）。

## 1. 技术选型

| 项 | 选择 | 理由 |
|---|---|---|
| 运行时 | .NET 8 (LTS) + WPF | 成熟、保留模式渲染、VirtualizingStackPanel 开箱即用 |
| 托盘 | Hardcodet.NotifyIcon.Wpf | WPF 托盘事实标准 |
| MVVM | CommunityToolkit.Mvvm | 官方维护，observable/command 免手写 |
| 发布 | self-contained 单文件 | 用户机免装运行时；代价 ~70MB（现 PyInstaller 29MB） |
| 安装器 | 沿用 Inno Setup | 同 AppId → v1.2.0 覆盖升级原地进行，data/ 保留 |
| 测试 | xunit + FlaUI(UIA) | 纯逻辑直测 + 真实点击路径（v1.2 教训：UI bug 必须走真实路径） |

依赖刻意收敛为上述两个 NuGet 包，其余全用 BCL/WPF。

## 2. 目录布局（同仓库，拍板前默认方案）

```
tools/
  workspace/          # Python v1.2.0 冻结，只读参考
  wpf/
    TodoTimer.sln
    TodoTimer/                # net8.0-windows, WPF, 单 EXE 双身份
      Models/                 #   Todo / TodoStore / TaskTree(纯函数) / RenderPlan
      Views/                  #   MainWindow / TimerPanelWindow (无边框置顶)
      ViewModels/
      Services/               #   SaveService(原子写) / IpcServer / Autostart /
                              #   SingleInstance / CliRunner
    TodoTimer.Tests/          # xunit
  installer/                  # installer.iss 原地改 SourceExe→wpf 发布产物
```

**版本号两处同步**规则平移：`TodoTimer.csproj` 的 `<Version>` ↔
`installer/installer.iss` 的 `MyAppVersion`（安装包名由 iss 派生，规则不变）。

## 3. 功能对等清单（验收基线 = v1.2.0）

- **主列表·每日一棵树**：ItemsControl + VirtualizingStackPanel，扁平条目
  （深度缩进）+ 日期组头（今日/日期 · N 项）+ 跨日子任务日期角标；
  树包含优先于日期分组、排序（重点→顺序）与完成态解耦——语义照搬
  `RenderPlan` 纯计算（v1.2 的 plan→diff 思想保留，diff 大半交给绑定）。
- **完成态视觉**：DataTrigger 切样式（绿底 + 绿 accent + `[已完成]` +
  灰字删除线），完成不移动任何卡片；无重绘补丁。
- **筛选**：默认全部 + 两勾选（未完成/已完成，可叠加）；谓词过滤 +
  命中者祖先链灰显保留——语义与 v1.2 一致。
- **任务树操作**：右键添加子任务（3 层上限）、Tab/Shift+Tab 降级提升、
  折叠箭头持久化、父卡片子进度 n/m、删整枝 + Ctrl+Z 整枝恢复、
  「清除已完成」归档时未完成后代上抛最近祖先。
- **拖拽三区**：上/下 25% 同父兄弟插入（蓝线）、中间 50% 成为子任务
  （绿框+缩进绿线）；防成环/超深拦截。**v1.2 bug 在此修复**（§6）。
- **计时**：悬浮面板（无边框 Topmost、顶部吸附、多任务并跑）、真实时钟、
  离线时间补算、目标时长提醒（弹窗/声音/通知）。
- **壳**：托盘（关闭=最小化、菜单真退出）、Ctrl+Q、开机自启
  （HKCU Run 同键）、文字搜索 300ms 防抖、日期导航与改期、快捷键全套。
- **CLI**：`cli list/add/done/undo/delete/start/pause/move/rename/star/
  search/stats/reparent/batch` 全命令 + `--json` + `--tree`，
  语义逐条对照 `todo_cli.py` / `cli_headless.py`。

### 建议裁剪（待拍板①）

- **分页（每页 30 + 加载更多）**：它是 Python 栈的渲染补丁；WPF 虚拟化
  天生只实例化可见项，万级条目直渲。建议**去掉**，界面少一个「加载更多」，
  其余不变。

## 4. 数据契约（不动）

- 位置（便携式，与现版一致）：`EXE 同目录 data/todos.json|archive.json`；
  开发运行指向仓库 `data/`（同现 `_BASE` 语义：frozen→exe 目录，
  源码运行→仓库根）。
- 格式：字段全保留（id/text/date/starred/order/running/started_at/elapsed/
  parent、顶层 collapsed）；旧数组格式兼容；装载时 normalize 自愈
  （悬挂父/成环/超深拍平）——`tree.py` 纯函数直译为 `TaskTree`。
- 原子写：`.tmp` → `File.Replace` → `.bak` 滚动备份。

## 5. CLI 与 IPC（契约不动，载体实现变化）

- **单 EXE 双身份**：`argv[1]=="cli"` → CliRunner（不启动 GUI）；
  否则单实例守卫（named Mutex）→ 已有实例则激活后退出。
- **控制台输出**：WPF 是 WinExe 无控制台，P/Invoke
  `AttachConsole(ATTACH_PARENT_PROCESS)` + 手写 stdout，输出统一 UTF-8
  （`--json` 由 Agent 解析，UTF-8 是唯一合理选择；待拍板⑤）。
- **IPC**：TCP 127.0.0.1:58372，消息 schema 与 `execute_batch` 语义逐条
  对照移植；三态规则原样——GUI 离线→headless 直读写；
  GUI 在线→IPC 即时上屏；**在线但 IPC 不可达→拒绝写文件防覆盖**。
- **数据目录注意**：CLI 写的是「该 EXE 旁边」的 data/——与现版行为一致
  （安装版写安装目录、开发版写仓库），不新增 %APPDATA% 迁移逻辑。

## 6. 拖拽三区重做（v1.2 bug 修复点）

- WPF 原生 `DragDrop` + `AdornerLayer` 画指示线/高亮框。
- 落点解析收敛为**纯函数** `ResolveDrop(items, index, yInItem)`：
  三区划分、同父约束、顶层同日期约束、防成环、3 层上限，全部入 xunit
  直测。v1.2 的教训是这段逻辑散在 Tk 事件回调里、合成事件测不出真实
  鼠标路径的差异——新版把它从事件层剥干净。
- 修复验收：真实鼠标拖拽（FlaUI 或手工清单）中间区变子级生效，
  合成事件与真实路径行为一致。

## 7. 测试策略

- **xunit（无 UI）**：TaskTree 自愈语义（对照 v1.2 测试断言移植）、
  TodoStore 新旧格式读写、RenderPlan（谓词/祖先链/分组/排序）、
  ResolveDrop、CliRunner headless（全部用临时目录数据副本，
  **绝不触碰仓库 `data/`**）。
- **FlaUI 冒烟**：真实点击路径覆盖增删改/完成变色/折叠/拖拽三区/托盘。
- **安装升级**：v1.2.0 → 1.4.0 覆盖安装，确认 data/ 保留、自启键沿用。

## 8. 里程碑（每步可运行）

| 里程碑 | 内容 | 出口判据 |
|---|---|---|
| M0 | 便携 SDK + 解决方案骨架 + 主窗口装载/存盘 todos.json | 读现有数据副本渲染出平铺列表 |
| M1 | 每日一棵树 + 完成态 + 筛选 + 搜索 | v1.2 视觉/语义对齐 |
| M2 | 树操作全套 + 拖拽三区（bug 修复）+ 删整枝撤销 + 归档 | §6 验收通过 |
| M3 | 计时面板 + 托盘 + 自启 + 单实例 + 提醒 | 常驻形态齐 |
| M4 | CLI + IPC + AttachConsole | `待办计时器.exe cli` 全命令与 v1.2 输出兼容 |
| M5 | self-contained 发布 + Inno Setup + 文档同步 | 安装包覆盖升级 v1.2.0 数据无损 |

## 9. 已拍板（2026-10-23 用户批复「可以」，均按建议值）

1. **分页去掉**：UI 虚拟化替代，界面不再有「加载更多」。
2. **发布方式**：self-contained 单文件（~70MB，免装运行时）。
3. **代码位置**：同仓库 `wpf/`，`workspace/` 冻结保留。
4. **CLI 输出编码**：统一 UTF-8。
5. **SDK 安装**：便携装 `D:\dotnet-sdk\`，不写系统目录不改 PATH（全路径调用）；
   装好后事实同步 `D:\AgentMemory` 公共记忆库（机器域）。

## 10. 风险

- **AttachConsole 边角**：CMD/PowerShell/Git Bash 下行为差异、用完须
  FreeConsole → M4 首项做三壳冒烟矩阵。
- **DPI**：现版有「双重缩放导致模糊」前科 → app.manifest 声明
  PerMonitorV2，WPF 原生处理，不手动二次缩放。
- **体积**：self-contained 安装包压缩后 ~35MB、占用 ~70MB，需接受
  （拍板②关联）。
- **FlaUI 拖拽**：UIA 模拟真实拖拽有物理轨迹依赖，必要时该环节人工清单兜底。
- **双线并存期**：M0–M4 期间 `installer/` 仍属 v1.2.0，M5 才切换 SourceExe；
  此前不动 iss（避免 Python 线没法出包）。
