# 待办计时器 UI 现代化设计（v1.1）

- **日期**：2026-06-30
- **状态**：已批准（待 spec 复核）
- **范围**：视图层现代化重写；不涉及数据/计时/存档逻辑
- **参考稿**：[`docs/mockup-main-window.html`](../../mockup-main-window.html)

---

## 1. 背景与目标

### 1.1 现状
当前使用纯 tkinter 手写了一套飞书风扁平 UI（`constants.py` 集中配色，`flat_btn`/`_mini_btn` 手搓扁平按钮）。底子不差，但有两个突出问题：

1. **布局截断**：底栏第一行 `r1` 塞 6 个扁平按钮，每个约 90px，合计约 600px，而主窗口仅 `560×660`。100% DPI 下擦边溢出，125%/150% 缩放下（笔记本常见）按钮被横向截断，显乱。
2. **整体显旧**：tkinter 原生控件气质（方角、默认斜面、经典滚动条/菜单），读起来像十年前的 Windows。纯精修能改善但去不掉。

### 1.2 目标
- 用 **customtkinter** 重写视图层，去掉"老 Windows"气质，达到现代扁平 UI 观感（见 HTML 预览稿）。
- 重做布局，根治按钮横向截断。
- 保持所有现有功能与交互不变。

### 1.3 非目标（YAGNI）
- 不做深色模式（本期仅浅色，`set_appearance_mode("light")`）。
- 不换平台、不引入 Web/Electron。
- 不改数据模型与计时/存档/托盘逻辑。
- 不新增功能。

---

## 2. 方案选择

经对比三条路径（A customtkinter 现代化重写 / B 纯 tkinter 深度精修 / C 分步），选定 **A：customtkinter 现代化重写**。

理由：用户核心痛点"像老 Windows"恰是只有 customtkinter（真圆角、扁平现代控件、现代滚动条）能根治的；纯精修（B）无法摆脱方角与经典控件气质。A 不属于"换平台"——仍是 Python 桌面 + PyInstaller 单 EXE，仅替换 UI 控件库。

---

## 3. 范围与边界

### 3.1 改动文件（视图层）
- `main_app.py`：主窗口、顶栏、日期栏、筛选、输入、工具栏、列表、对话框
- `todo_card.py`：`TodoCard` / `SectionHeader` / `EmptyHint`
- `timer_panel.py`：`TimerPanel` + `TimerRow`（计时悬浮面板）
- `date_picker.py`：日期选择对话框
- `tooltip.py`：悬浮提示
- `main.py`：DPI 处理调整（见 §8.1）
- `constants.py`：配色 token 映射为 customtkinter 参数（颜色值不变）

### 3.2 不改动
- `todo_widget.py`（纯数据模型，一行不动）
- `tray.py`（pystray 原生托盘菜单）
- `utils.py` 的格式化函数（`flat_btn` 视图层 helper 随按钮迁移调整或弃用）
- 计时、Observer 回调、原子存档、运行恢复、拖拽排序、顶部吸附状态机等逻辑

---

## 4. 依赖与打包

- 新增运行时依赖：`customtkinter`。
- PyInstaller 命令新增 `--collect-all customtkinter`（与现有 `--collect-all pystray` 并列）：

  ```bash
  pyinstaller --onefile --noconsole --name "待办计时器" \
    --collect-all pystray --collect-all customtkinter \
    --hidden-import six.moves \
    --distpath "../" --workpath "../build_tmp" main.py
  ```

- 预期 EXE 增大数 MB。

---

## 5. 视觉设计系统

### 5.1 配色（沿用 `constants.py` 飞书色板，映射为 CTk 颜色参数）
- 主色 blue `#3370FF`（hover `#4E83FD`，soft `#E1EAFF`）
- 底 `bg #F5F6F7` / 面 `surface #FFFFFF`
- 文字 `text #1F2329` / `text_sec #646A73` / `text_ter #8F959E`
- 语义 green `#00B578`、orange `#FF8F1F`、red `#F53F3F`（含 light/hover 变体）
- 边框 `border #E5E6EB`，删除线 `strikethrough #BBBBC4`

### 5.2 形状
- 卡片 / 输入框 `corner_radius = 10`
- 按钮 `corner_radius = 8`
- 药丸（筛选段、状态标签）`corner_radius = 999`

### 5.3 阴影与层次
- 窗口投影、卡片极轻投影（CTkFrame 分层 + 细边框模拟）。
- 计时面板由原"假阴影色块"改为真层次感（`fg_color` 分层 + 细边框）。

### 5.4 字体
- 正文 `Microsoft YaHei`；时间/数字 `Consolas`。

### 5.5 控件映射
| 现状（tk 合成） | customtkinter |
|---|---|
| `flat_btn`（Frame+Label） | `CTkButton`（fg_color / hover_color） |
| `_mini_btn` | `CTkButton`（小尺寸） |
| `tk.Entry` | `CTkEntry` |
| `tk.Scrollbar` | 现代滚动条（CTk 样式） |
| `tk.Frame` 容器 | `CTkFrame` |
| 筛选 Label chips | `CTkSegmentedButton`（或自绘药丸） |
| `tk.Tk()` | `CTk()` |
| `tk.Toplevel`（面板/对话框/虚影） | `CTkToplevel` |

语义保留：左侧色条、① 序号、★ 重点、●○✓ 状态、⏰ 倒计时——含义不变，仅换容器与控件。

---

## 6. 布局重设计

### 6.1 主窗口
- 默认尺寸 `560×660` → **`720×780`**；`minsize` → **`560×620`**；可自由缩放。
- 结构（自上而下）：标题栏 → 日期 + 统计行 → 分段筛选 → 输入卡 → 主操作工具栏 → 选中操作图标栏 → 列表头 + 搜索 → 卡片列表。

### 6.2 工具栏（根治截断）
拆为两段：
- **主操作（文字按钮）**：`▶ 全部开始`（绿）/ `⏸ 全部暂停`（橙）靠左；`🗄 清除已完成`（弱化样式）靠右。
- **选中操作（纯图标 + tooltip）**：`★ ✓ ✎ 📅` │ `↑ ↓` │ `🗑`，分组以细竖线分隔，约 32–36px/个。150% DPI 下亦不溢出。这些操作同时存在于卡片右键菜单，工具栏仅作快捷入口。

> **决策**：选中类操作采用**纯图标 + 悬停提示**（用户已确认）。

### 6.3 卡片
圆角 + 极轻投影 + 左色条（蓝 = 进行 / 绿 = 完成 / 橙 = 重点 / 灰 = 普通）；选中 = 浅蓝底；hover = 微灰底；文字溢出省略号 + tooltip。

---

## 7. 逐组件迁移要点

- **main_app.py**：`tk.Tk()` → `CTk()`，`set_appearance_mode("light")`；按 §6 重建布局；`flat_btn` 调用点全部换 `CTkButton`；筛选换 `CTkSegmentedButton`；右键菜单 `tk.Menu` 保留（仍走原生，不在 CTk 化范围）。
- **todo_card.py**：`TodoCard` → `CTkFrame` 圆角卡；`set_selected` / `_on_enter` / `_on_leave` 原先的 bg 递归替换改为 CTk `fg_color` 切换；色条、序号、状态、⏰ 逻辑不变。
- **timer_panel.py**：`Toplevel` → `CTkToplevel`，保留 `overrideredirect` + 顶部吸附状态机 + 动画 + `_check_leave` 坐标守卫；`TimerRow` 控件换 CTk。
- **date_picker.py / 编辑对话框**：`Toplevel` → `CTkToplevel`，按钮换 `CTkButton`。
- **tooltip.py**：`Toplevel` → `CTkToplevel`（或保留 `tk.Toplevel` 以兼容）；样式沿用深底白字。
- **拖拽虚影 / 落点指示线**：临时 `Toplevel` 一并迁 `CTkToplevel`。

---

## 8. 兼容性风险

### 8.1 DPI 缩放冲突（重点）
`main.py` 现调用 `SetProcessDpiAwareness(2)`（Per-Monitor V2），而 customtkinter 自带 DPI 控件缩放，两者叠加会导致双重放大 / 模糊。实施时需确定**单一缩放源**（二选一）：
- 移除手动 DPI 调用，让 CTk 接管；或
- 保留 Per-Monitor V2，并显式关闭 CTk 自动缩放。

打包后在 125% / 150% 屏实测，确认无模糊、无双重缩放。

### 8.2 无边框面板
`CTkToplevel` 支持 `overrideredirect`；顶部吸附依赖负坐标置窗（CLAUDE.md 已记录无边框窗口可置负坐标），需复测吸附与展开/收起动画。

### 8.3 跨线程与滚轮
- pystray 跨线程回调仍经 `root.after` / `_pump_tray` 投递，不变。
- 滚轮 `bind_all` + `_on_mousewheel` 坐标守卫保留，避免面板与主窗口串扰。
- Canvas 内嵌 Frame 的 `<Configure>` 手动更新 scrollregion 逻辑保留。

---

## 9. 验证（人工，无自动化测试）

- [ ] 主窗口 `720×780` 打开，所有按钮可见、无截断（100% / 125% / 150% DPI 各测一次）。
- [ ] 圆角 / 阴影 / 现代滚动条观感符合 HTML 预览稿。
- [ ] 计时准确（`sleep ≥ 1.2s` 后核对，CLAUDE.md 易错点）。
- [ ] 计时面板：顶部吸附、展开/收起、拖拽、坐标守卫正常。
- [ ] 拖拽排序、落点指示线、5px 阈值点击区分正常。
- [ ] 系统托盘最小化/恢复/退出、开机自启正常。
- [ ] 原子保存 / `.bak` 回退 / 运行恢复正常。
- [ ] `remove_callback` 精确移除（防内存泄漏）。
- [ ] 打包 EXE 在高 DPI 真机运行，确认无模糊、无双重缩放。

---

## 10. 参考稿
`docs/mockup-main-window.html`（已与用户确认，作为视觉验收基准）。
