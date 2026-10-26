⚠ 2026-10-26 迁移：实现已从 Python(workspace/) 迁至 C#/WPF(wpf/)，v1.4.0。本文件下述 Python 架构/命令仅作历史参考；当前权威=wpf/ 代码与 docs/product/v1.4/design.md。

# CLAUDE.md

## 命令

```bash
cd tools/workspace && python main.py                    # 开发运行
pyinstaller --onefile --noconsole --name "待办计时器" --collect-all pystray --collect-all customtkinter --hidden-import six.moves --icon "../installer/app.ico" --distpath "../" --workpath "../build_tmp" main.py
rm -rf ../build_tmp 待办计时器.spec                      # 打包 → ../待办计时器.exe（含图标）
installer\build_installer.bat                           # 一键打安装器（需 Inno Setup 6）→ ../待办计时器_Setup_1.1.0.exe
python installer/make_icon.py                           # 仅重生成 app.ico
```

Python 3.10+ 标准库 + pystray/pillow（系统托盘）。

## 架构

**数据流**: `用户操作 → MainApp → TodoWidget._fire_change() → _on_todo_state_change() → _refresh_view() + timer_panel.refresh_all() + _ensure_main_tick() + _schedule_save()` (300ms 防抖)

**三层分离**: `todo_widget.py` 纯数据模型 (无 tkinter) / `timer_panel.py` 计时 UI / `main_app.py` + `todo_card.py` 列表 UI (计划比对 + 原地增删 + 流式全量，见渲染管线) / `tree.py` 任务树纯函数（只操作 {id: parent_id} 映射，GUI 与 headless 共用，禁止 import TodoWidget/tkinter）

**双 Tick**:
- 计时面板 `timer_panel._tick()` 200ms 刷新面板内所有行。
- 主窗口 `_main_tick()` 200ms 仅更新运行中卡片的时间标签（`set_time_text`/`update_reminder` 同值守卫），不重建视图（避免闪烁）。
- 二者都「有 running 就持续，全停自动停止」。

**Observer 回调**: TodoWidget 持有 `_callbacks` 列表（非单一回调）。MainApp 和 TimerPanel 各自通过 `add_callback()` / `remove_callback()` 订阅。`_fire_change()` 遍历全部触发。移除待办时 TimerPanel 用保存的 callback 引用精确移除自己那条。

**CLI/Agent 服务**: GUI 启动时内嵌 `src/cli_server.py`（TCP 127.0.0.1:58372，一行 JSON 请求/响应；环境变量 `TODOTIMER_CLI_PORT` 覆盖）。命令经 `_ipc_queue` + `_pump_tray` 投递主线程执行（`_cli_*` 处理器：list(支持 search/date_from/date_to 过滤)/stats/add/delete/move/reparent/done/undo/rename/star/start/pause；delete 等五命令接受批量 id），与渲染/存盘管线同源——Agent 改动即时上屏，GUI 操作对 Agent 立即可见。CLI start 会把任务加入计时面板（与 GUI 启动路径一致）；start/pause 对已完成/已在运行的任务如实报错/提示，不误报 running。v1.2 树语义：list 输出含 `parent`/`depth`、DFS 森林序（`_tree_ordered`，与 GUI 显示一致）；`add --parent`、`reparent (--parent|--top)` 经 `tree.can_adopt` 校验（成环/超 3 层拒绝）；`delete` 删整枝（回显 count），批量 id 同枝时后续 id 报错但不影响已删部分。入口 `workspace/todo_cli.py`（打包后 `待办计时器.exe cli ...`，见 `main.py`）：GUI 在线走 IPC；连接被拒（GUI 未运行）降级 `src/cli_headless.py` 直接读写 JSON（同语义：done 归今天、原子写+.bak，树语义与 GUI 共用 `tree.py`）。注意：GUI 运行中但 IPC 不可达时 CLI 拒绝写文件（防覆盖），仅 list 允许降级读。

**渲染管线 (v1.2)**: `_refresh_view` 先 `_compute_plan()` 纯计算渲染计划（entries + 统计文本 + 数据指纹，指纹含不走 `_fire_change` 的 `starred/_order/parent_id` 直接赋值），计划与上次相同则整体跳过（吸收 observer 双订阅与操作后显式调用的重复刷新）。卡片条目为 9 元组 `("card", tid, seq, show_date, depth, expandable, expanded, dim, 子进度)`。落地上分三路：卡片序列不变 → `_sync_card` 同值守卫逐卡同步；纯增/纯删 → 只增删目标卡片（`pack(before=/after=)` 锚点重挂轻量装饰；一次新增 >10 张经 `_insert_step` 分帧补齐，装饰重挂延到最后一帧）；其余（切视图/排序/折叠）→ 全量重排，首屏 14 条同步 + 剩余按 80ms 预算分帧补齐（`_render_seq` 令牌使新计划即时作废旧流）。**分页**在计划层截断：每页最多 `PAGE_SIZE=30` 张卡片，尾部 `("more", …)` 哨兵渲染为「加载更多」，`_load_more` 增加页容量 → 天然走 insert 原地路径；滚动到底经 `_on_canvas_yview` 防抖自动加载。选中状态跨刷新保留（目标卡片仍在当前视图渲染即保留并高亮；删除/归档路径须显式清空）。启动空闲期 `_prewarm_cards` 逐张 `place(x=-20000)` 完成首次映射再卸下（上限 200 张）。卡片仅外壳是 CTk（圆角/选中/悬停），内部标签与分组标题、空提示、加载更多全为纯 tk，底色统一由 `TodoCard._apply_fg` 同步。

**任务树与筛选 (v1.2)**: 每条任务带 `parent`（null=顶层），兄弟序=全局 `order`，层级上限 3（`tree.MAX_DEPTH_INDEX=2`）。布局=**不分区，每日一棵树**：按日期倒序日组，组内是当天顶层任务的森林，子树跟根走（树包含优先于日期分组，跨日子任务显示日期角标）；排序 `（非重点, order）` 与完成态解耦，完成/取消不移动卡片。勾选筛选（`_filter_active_var`/`_filter_completed_var` → `filter_state`）与搜索是节点谓词：命中者保留、命中者的祖先链强制保留（`dim` 灰显）、其余剔除；默认全不勾=all。启动默认 `filter_state="all"`。折叠集 `_collapsed` 持久化到 `todos.json` 顶层 `collapsed`。装载时 `tree.normalize` 自愈悬空/环/超深（拍平为顶层）。完成态=绿底 `green_light`+灰字（底色优先级 选中>完成>悬停>白，完成卡不响应悬停）。

**顶部吸附**: `none → docked+collapsed (拖到顶≤60px) → docked+expanded (鼠标进入) → docked+collapsed (离开500ms)`。`_check_leave()` 用坐标比对过滤子控件假 Leave。

**运行恢复**: 保存 `running + last_started_at`，加载时补算离线时间 `_base_elapsed += int(time.time() - last_started_at)`。

**日期更新**: `set_complete()` 自动将 `_date` 设为当天（跨日完成归到完成日）；`set_date()` 改期。

**归档**: `_archive_completed()` 把所有已完成项移到 `data/archive.json`（带 `archived_at`），从主列表删除；被归档节点的未完成后代自动上抛给最近的未归档祖先。

**删除与撤销**: 删除含子任务的任务 = 整枝删除（确认框含子任务数/累计耗时），Ctrl+Z 快照按父先子后恢复树引用。

**系统托盘**: `tray.py` 用 pystray 跑独立线程，菜单回调经 `root.after(0,…)` 投递回主线程。关闭按钮=`_minimize_to_tray`（含面板，刷新存盘）；真正退出走托盘菜单或 `Ctrl+Q` → `_quit_app`（`destroy()` 让 mainloop 自然结束，不 `sys.exit`）。开机自启读写 HKCU `…\Run`。无 pystray 时关闭按钮退化为退出。

**计时提醒**: `target_seconds` 目标时长。`_main_tick` 检查 `should_remind()` → `_fire_reminder`（Beep+托盘通知+模态弹窗），触发后置 `reminded=True` 防重复；`reset()`/`set_target()` 清零允许再触发。

**拖拽排序 (v1.2 三区落点)**: 整卡拖拽+5px 阈值区分点击。`_resolve_drop` 按指针在目标卡片内的纵向位置分三区——上 25%/下 25% = 同父兄弟插入（顶层维持同日期规则，蓝色指示线）、中间 50% = 成为其子任务（`can_adopt` 校验，目标绿框+缩进绿线）；无效落点无反馈、松手弹回。拖拽落点基于当前已渲染卡片（分页窗口内），跨页边界不可达。全局 `ButtonRelease-1` 安全网清理拖拽高亮。

**数据兼容**: 格式 `{"panel_x": ..., "panel_y": ..., "autostart": ..., "collapsed": [...], "todos": [{"parent": null, ...}]}`；`parent`/`collapsed` 为 v1.2 纯增量字段——旧版程序读新文件忽略未知键，新版读旧文件默认 `parent=null`、全展开。旧版纯数组通过 `isinstance(raw, list)` 自动转换。

**原子保存**: `.tmp` → `os.replace()` → 复制 `.bak`。加载损坏时回退 `.bak`。

**版本号**: `constants.APP_VERSION`（当前 `1.2.0`），顶栏标题右侧显示；安装器升级检测与安装包文件名同源。改版本时两处同步：`constants.py` 与 `installer/installer.iss` 的 `MyAppVersion`（安装包输出名由 `.iss` 的 `OutputBaseFilename` 派生）。

**安装器**: `installer/installer.iss`（Inno Setup 6，UTF-8 BOM）。单用户免管理员，默认装 `{localappdata}\Programs\待办计时器`，向导可改路径；固定 `AppId` → 重复运行原地覆盖升级（升级前 `taskkill` 释放 EXE 占用）。`data/` **不打入安装包**，由程序首次运行自建 → 升级保留用户数据；卸载弹窗可选删除 `data/`（`SuppressibleMsgBox` 默认「否」→ 静默卸载不弹窗且保留数据）。`installer/build_installer.bat` 串起 图标→PyInstaller→iscc；`installer/make_icon.py` 用 PIL 生成 `app.ico`（蓝底白色钟表）。简中向导语言文件 `installer/ChineseSimplified.isl` **本地随包**（kira-96 译，适用 6.5.0+，UTF-8 BOM）——Inno 官方安装包不含中文，故 `.iss` 的 `[Languages]` 不能用 `compiler:` 前缀，改为引用本地文件。

## 易错点

- `int(time.time())` 截断亚秒差为 0，测试计时需 sleep ≥1.2s
- 无边框窗口可置屏幕负坐标，吸附依赖此特性
- TimerRow 时间 Label 先于文本 pack (RIGHT 优先)，保时间不被挤出
- Canvas 内嵌 Frame 的 `<Configure>` 事件需手动更新 scrollregion
- Observer 回调用列表存引用，TimerPanel 必须用 `remove_callback()` 精确移除自己的那条，否则内存泄漏
- CTk 控件 configure 同值也触发整控件重绘 —— 高频路径（每 200ms tick、批量渲染）的 configure 必须同值守卫
- Canvas 窗口 item 内的容器若在 `<Configure>` 里直接联动 `scrollregion`，批量重排会被放大 ~7 倍（逐卡整幅重绘）—— 必须 50ms 合并（`_on_card_frame_configure` / `TimerPanel._on_inner_configure`）
- CTk 卡片的首次映射（canvas 初始化+尺寸协商）远贵于重映射 —— 预热须真实映射一次（place 可视区外），仅创建控件不够
- 纯 tk 子控件 bg 不透明，卡片换底色（选中/悬停/完成绿）时须同步子控件 bg（`TodoCard._apply_fg`）
- Tk 对 500ms 内同一控件的第二次 press 只派发最特异的 `<Double-Button-1>`（不发 `<Button-1>`）——折叠箭头已同时接管 Button-1 与 Double-Button-1；给新控件挂点击时若同一区域也挂了 Double 语义，务必两者一致处理
- `event_generate` 合成事件打在 CTk 外壳（CTkFrame）上不会到达内部 canvas 的绑定——GUI 自动化测试要把事件打在纯 tk 子控件（如 `card.text_lbl`）上；`event_generate` 的屏幕坐标参数是 `-rootx/-rooty`（不是 `y_root`）
- `MainApp.filter_state` 启动默认 `"all"`（v1.2 勾选筛选）——别照旧例改回 `"active"`
- `installer.iss` 必须存 UTF-8 BOM（含中文 AppName，否则 Inno 按 GBK 误读成乱码）；`ChineseSimplified.isl` 同样需 UTF-8 BOM（否则中文系统按 GBK 误读向导文字）；`build_installer.bat` 为 GBK+CRLF（中文 Windows cmd 才能正确解析中文 EXE 名）

## 文档同步

代码或行为有变更时，必须同步更新对应文档，避免文档与代码漂移：

- **版本号**：改 `constants.APP_VERSION` → 同步 `installer/installer.iss` 的 `MyAppVersion`、`build_installer.bat` 输出名、`docs/CHANGELOG.md`、以及 `docs/product/current.md` 与 `docs/development/current.md` 的「当前生效版本」。
- **用户可见变更**（新功能 / 行为变化 / UI 调整）→ 写进 `docs/CHANGELOG.md` 摘要，详细变更进 `docs/product/<版本>/changelog.md`。
- **架构或约束变化**（数据流、模块职责、构建/打包流程、新增易错点）→ 更新本文件对应段落。
- **路线图**：已完成的功能从 `docs/ROADMAP.md`「候选」挪到「已完成」。
