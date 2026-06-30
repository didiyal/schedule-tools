# 待办计时器 UI 现代化（v1.1）实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 用 customtkinter 重写待办计时器视图层，去掉"老 Windows"气质并根治工具栏横向截断，所有现有功能与交互保持不变。

**Architecture:** 保持三层分离（`todo_widget.py` 数据层不动）。视图层（`main_app.py / todo_card.py / timer_panel.py / date_picker.py / tooltip.py`）的 tkinter 控件整体替换为 customtkinter（`CTk/CTkButton/CTkEntry/CTkFrame/CTkToplevel` 等），配色沿用 `constants.py` 飞书色板。主窗口布局重排，工具栏拆为"主操作文字按钮 + 选中操作纯图标栏"，窗口加大到 720×780。

**Tech Stack:** Python 3.10+、customtkinter（新增）、pystray/pillow（托盘，不动）、PyInstaller 打包。

## Global Constraints

- **不改动数据层**：`todo_widget.py` 一行不动；计时 / Observer 回调 / 原子存档 / 运行恢复 / 拖拽排序 / 顶部吸附状态机逻辑全部保留。
- **不做深色模式**：`set_appearance_mode("light")`。
- **配色值不变**：沿用 `constants.py` 的 `C` 与 `FONT`；新增圆角 token。
- **DPI 单一缩放源**：DPI 感知交给 customtkinter，`main.py` 不再手动 `SetProcessDpiAwareness`（spec §8.1）。
- **验证方式**：本项目无自动化测试（spec §9）。每个任务以「运行 + 对照预览稿/清单人工核对 + 提交」收尾，不写单元测试。
- **开发运行命令**：`cd tools/workspace && python main.py`
- **Python 环境要求**：先 `pip install customtkinter`（托盘依赖 pystray/pillow 已有）。

## File Structure

| 文件 | 责任 | 本计划动作 |
|---|---|---|
| `workspace/src/constants.py` | 配色 + 字体 + 圆角 token | 新增 `RADIUS` |
| `workspace/main.py` | 入口 + DPI | 移除手动 DPI 块 |
| `workspace/src/utils.py` | `flat_btn` 等视图 helper | `flat_btn` 改基于 `CTkButton` |
| `workspace/src/main_app.py` | 主窗口 UI + 业务编排 | 根 `CTk()` + 布局重排 + 控件迁移 |
| `workspace/src/todo_card.py` | 卡片 / 区块头 / 空提示 | `CTkFrame` 圆角卡 |
| `workspace/src/timer_panel.py` | 计时悬浮面板 | `CTkToplevel` + 控件迁移，保留吸附 |
| `workspace/src/date_picker.py` | 日期对话框 | `CTkToplevel` + `CTkButton` |
| `workspace/src/tooltip.py` | 悬浮提示 | `CTkToplevel`（或保留 `tk.Toplevel`） |
| `workspace/src/tray.py` | pystray 托盘 | **不动** |
| `workspace/src/todo_widget.py` | 数据模型 | **不动** |

---

## Task 1: 安装依赖 + 扩展 constants 圆角 token

**Files:**
- Modify: `workspace/src/constants.py`
- Verify env: `pip install customtkinter`

**Interfaces:**
- Produces: `from .constants import RADIUS`，值为 `{"card":10,"button":8,"pill":999}`，供后续所有任务引用。

- [ ] **Step 1: 安装 customtkinter**

Run: `pip install customtkinter`
Expected: 成功安装（确认可 `python -c "import customtkinter; print(customtkinter.__version__)"`）。

- [ ] **Step 2: 在 constants.py 末尾追加圆角 token**

在 `workspace/src/constants.py` 的 `FONT = "Microsoft YaHei"` 行之后追加：

```python
# 圆角（customtkinter corner_radius 用）—— spec §5.2
RADIUS = {
    "card": 10,
    "button": 8,
    "pill": 999,
}
```

- [ ] **Step 3: 校验导入**

Run: `cd tools/workspace && python -c "from src.constants import C, FONT, RADIUS; print(RADIUS)"`
Expected: 打印 `{'card': 10, 'button': 8, 'pill': 999}`，无报错。

- [ ] **Step 4: 提交**

```bash
git add workspace/src/constants.py
git commit -m "feat(ui): 新增圆角 token RADIUS（v1.1 视觉系统）"
```

---

## Task 2: CTk 根窗口 + DPI 单一缩放源 + 窗口尺寸

**Files:**
- Modify: `workspace/main.py:1-18`
- Modify: `workspace/src/main_app.py:1-32`（导入与根窗口创建段）

**Interfaces:**
- Produces: `MainApp.root` 为 `customtkinter.CTk()` 实例；外观锁定浅色；默认 720×780，minsize 560×620。后续任务在此基础上挂 CTk 子控件。

- [ ] **Step 1: main.py 移除手动 DPI 块**

把 `workspace/main.py` 整体替换为（DPI 感知交给 customtkinter 在创建 `CTk()` 时统一设置，避免双重缩放，spec §8.1）：

```python
"""待办计时器 - 入口"""
# DPI 感知由 customtkinter 在创建 CTk() 时统一设置，此处不再手动调用
# SetProcessDpiAwareness，避免双重缩放导致模糊/过大（见 spec §8.1）。
from src.main_app import MainApp

if __name__ == "__main__":
    app = MainApp()
    app.run()
```

- [ ] **Step 2: main_app.py 切换根窗口为 CTk**

修改 `workspace/src/main_app.py` 顶部导入（约 17 行 `from .constants import C, FONT`）改为：

```python
import customtkinter as ctk
from .constants import C, FONT, RADIUS
```

修改 `__init__` 内根窗口创建段（约 28-32 行）：

```python
        self.root = ctk.CTk()
        self.root.title("待办计时器")
        ctk.set_appearance_mode("light")
        ctk.set_default_color_theme("blue")  # 仅取结构，颜色由各控件 fg_color 覆盖
        self.root.geometry("720x780")
        self.root.minsize(560, 620)
        self.root.configure(fg_color=C["bg"])
```

> 注意：`CTk()` 是 `tk.Tk` 的子类，现有 `tk.Frame/Label` 子控件此时仍可临时挂载，应用可运行（只是控件仍为旧 tk 样式，后续任务逐个迁移）。

- [ ] **Step 3: 运行验证**

Run: `cd tools/workspace && python main.py`
Expected: 窗口以 720×780 打开，浅色背景，无崩溃。控件仍为旧样式（本任务不要求美观）。

- [ ] **Step 4: DPI 验证（关键，spec §8.1）**

在 Windows 显示设置里把缩放调到 125% 与 150% 各运行一次。
Expected: 窗口与文字清晰不模糊、未被双重放大。若出现模糊/过大 → 回退方案：在 `main.py` 重新加回 `ctypes.windll.shcore.SetProcessDpiAwareness(2)` 并在 `main_app.py` 根创建后调用 `ctk.set_widget_scaling(1.0)` 抑制 CTk 自动缩放，再次验证。

- [ ] **Step 5: 提交**

```bash
git add workspace/main.py workspace/src/main_app.py
git commit -m "feat(ui): 根窗口迁移 CTk + 尺寸 720x780 + DPI 单一缩放源"
```

---

## Task 3: flat_btn 改为基于 CTkButton

`flat_btn` 被 `main_app.py`（添加/底栏按钮）与 `date_picker.py` 调用。改写实现即可让所有调用点自动获得圆角现代按钮，调用签名保持不变。

**Files:**
- Modify: `workspace/src/utils.py:1-19`（`flat_btn` 函数）

**Interfaces:**
- Produces: `flat_btn(parent, text, cmd, bg, hover_bg, font_size, pady, padx, fg)` 返回 `customtkinter.CTkButton`，保留原签名，供 main_app / date_picker 无痛替换。

- [ ] **Step 1: 重写 flat_btn**

把 `workspace/src/utils.py` 顶部的 `import tkinter as tk` 之后增加导入，并整体替换 `flat_btn` 函数为：

```python
import customtkinter as ctk
from .constants import FONT, RADIUS


def flat_btn(parent, text, cmd, bg, hover_bg,
             font_size=10, pady=6, padx=18, fg="#FFFFFF"):
    """飞书风扁平按钮 —— 基于 CTkButton（圆角 + hover 平滑过渡）。
    保留原签名，padx 由 CTkButton 内边距自动吸收。"""
    height = font_size + 2 * pady + 6  # 近似原 pady 撑高
    btn = ctk.CTkButton(
        parent, text=text, command=cmd,
        fg_color=bg, hover_color=hover_bg, text_color=fg,
        corner_radius=RADIUS["button"],
        font=(FONT, font_size),
        border_width=0,
        height=height,
    )
    return btn
```

- [ ] **Step 2: 运行验证**

Run: `cd tools/workspace && python main.py`
Expected: 不崩溃；输入区"＋ 添加待办"、日期栏"今天"等 `flat_btn` 按钮变为圆角扁平按钮，hover 有平滑过渡、颜色正确。

- [ ] **Step 3: 提交**

```bash
git add workspace/src/utils.py
git commit -m "feat(ui): flat_btn 迁移至 CTkButton（圆角 + hover 过渡）"
```

---

## Task 4: 主窗口上半区迁移（顶栏 / 日期栏 / 分段筛选 / 输入卡）

**Files:**
- Modify: `workspace/src/main_app.py:80-163`（`_build_ui` 顶栏→输入卡段）
- Modify: `workspace/src/main_app.py:355-365` 与 `:471-477`（筛选态刷新逻辑，改为操作 `CTkSegmentedButton`）

**Interfaces:**
- Produces: `self.filter_seg`（`CTkSegmentedButton`，值"未完成/已完成/全部"），通过 `self._on_seg_filter(value)` 驱动 `self.filter_state`。

- [ ] **Step 1: 顶栏与分隔改为 CTkFrame**

`_build_ui` 内（约 82-93 行）把顶栏容器与分隔线替换：

```python
        hdr = ctk.CTkFrame(self.root, fg_color=C["surface"], height=48, corner_radius=0)
        hdr.pack(fill=tk.X)
        hdr.pack_propagate(False)

        ctk.CTkLabel(hdr, text="待办计时器", fg_color=C["surface"],
                     text_color=C["text"], font=(FONT, 15, "bold")).pack(
            side=tk.LEFT, padx=20, pady=10)

        self.stat_lbl = ctk.CTkLabel(hdr, text="", fg_color=C["surface"],
                                     text_color=C["text_sec"], font=(FONT, 10))
        self.stat_lbl.pack(side=tk.RIGHT, padx=20, pady=10)

        ctk.CTkFrame(self.root, fg_color=C["border"], height=1).pack(fill=tk.X)
```

- [ ] **Step 2: 日期导航栏迁移**

约 96-122 行 `date_bar` 段：容器 `tk.Frame`→`ctk.CTkFrame(fg_color=C["surface"])`；`btn_prev/btn_next` 由 `tk.Label` 改为 `ctk.CTkLabel`（保留 `‹` `›` 文本、`text_color=C["blue"]`、`font=(FONT,20,"bold")`、cursor hand2、`<Button-1>` 绑定不变）；`date_lbl`→`CTkLabel`；`day_total_lbl`→`CTkLabel`。"今天"按钮已是 `flat_btn`（Task 3 自动升级），保持。

- [ ] **Step 3: 筛选 chips → CTkSegmentedButton**

把约 124-142 行的筛选 chip 构建循环替换为分段控件：

```python
        filter_bar = ctk.CTkFrame(self.root, fg_color=C["surface"])
        filter_bar.pack(fill=tk.X)
        filter_inner = ctk.CTkFrame(filter_bar, fg_color=C["surface"])
        filter_inner.pack(fill=tk.X, padx=16, pady=(0, 8))

        self._seg_map = {"未完成": "active", "已完成": "completed", "全部": "all"}
        self.filter_seg = ctk.CTkSegmentedButton(
            filter_inner,
            values=["未完成", "已完成", "全部"],
            command=self._on_seg_filter,
            corner_radius=RADIUS["pill"],
            selected_color=C["blue"], selected_hover_color=C["blue_hover"],
            unselected_color=C["bg"], unselected_hover_color=C["border"],
            text_color="#FFFFFF", font=(FONT, 11),
        )
        self.filter_seg.set("未完成")
        self.filter_seg.pack(side=tk.LEFT)
```

新增方法（放在 `_set_filter` 附近）：

```python
    def _on_seg_filter(self, value):
        self._set_filter(self._seg_map[value])
```

- [ ] **Step 4: 清理旧 chip 刷新逻辑**

删除/改写引用 `self.filter_chips` 的两处（约 360-365 行 `_set_filter` 内、471-477 行 `_refresh_filter` 内）：移除对 `self.filter_chips[k].configure(...)` 的调用；如需同步选中态，改用 `self.filter_seg.set(<中文键>)`（由 `filter_state` 反查 `_seg_map` 的逆映射）。

- [ ] **Step 5: 输入卡迁移**

约 147-163 行：`card_inp/card_inp_inner` 容器→`CTkFrame(fg_color=C["surface"])`；`self.entry` 由 `tk.Entry` 改为：

```python
        self.entry = ctk.CTkEntry(card_inp_inner, fg_color=C["bg"], text_color=C["text"],
                                  border_color=C["border"], border_width=1,
                                  corner_radius=RADIUS["card"], font=(FONT, 12), height=36)
        self.entry.pack(side=tk.LEFT, fill=tk.X, expand=True, padx=(0, 10))
        self.entry.bind("<Return>", lambda e: self._add_todo())
        self.entry.focus_set()
```

`＋ 添加待办` 按钮已是 `flat_btn`，保持。

- [ ] **Step 6: 运行验证**

Run: `cd tools/workspace && python main.py`
Expected: 顶栏、日期栏、分段筛选（药丸式，"未完成"高亮）、输入卡均为现代圆角样式；点击筛选段能切换列表；不崩溃。

- [ ] **Step 7: 提交**

```bash
git add workspace/src/main_app.py
git commit -m "feat(ui): 主窗口上半区迁移 CTk（顶栏/日期/分段筛选/输入卡）"
```

---

## Task 5: 工具栏重设计（根治横向截断）

**核心改动**：原底栏两行 10 个文字按钮溢出。改为「主操作文字按钮 + 选中操作纯图标栏」，并在图标按钮上挂 tooltip。

**Files:**
- Modify: `workspace/src/main_app.py:165-207`（`_build_ui` 底栏 `bf/r1/r2` 段）

**Interfaces:**
- Produces: 一组图标 `CTkButton`（`★ ✓ ✎ 📅 ↑ ↓ 🗑`），通过 `ToolTip`（Task 9 后为 CTk 版，本任务先用现有 `tk.Toplevel` 版 tooltip 亦可）提供文字提示；回调复用现有 `_toggle_star_selected / _toggle_complete_selected / _edit_selected / _change_date / _move_up_selected / _move_down_selected / _delete_selected`。

- [ ] **Step 1: 新增图标按钮 helper**

在 `main_app.py` 内（模块级，`_build_ui` 之外）新增：

```python
def _icon_btn(parent, text, cmd, tip, danger=False):
    """纯图标圆角按钮 + tooltip。danger=True 时 hover 变红。"""
    btn = ctk.CTkButton(
        parent, text=text, command=cmd, width=32, height=32,
        fg_color="transparent", hover_color=(C["red_light"] if danger else C["blue_light"]),
        text_color=(C["red"] if danger else C["text_sec"]),
        corner_radius=RADIUS["button"], font=(FONT, 14), border_width=0,
    )
    ToolTip(btn, lambda: tip)  # 复用现有 tooltip
    return btn
```

> 需在 main_app.py 顶部导入 `from .tooltip import ToolTip`（若尚未导入）。

- [ ] **Step 2: 重写底栏为主操作 + 图标栏**

把约 165-207 行（`bf / r1 / r2` 整段）替换为：

```python
        # ── 工具栏（主操作 + 选中操作图标栏）—— 根治横向截断 ──
        bf = ctk.CTkFrame(self.root, fg_color=C["bg"], corner_radius=0)
        bf.pack(side=tk.BOTTOM, fill=tk.X, padx=20, pady=(0, 12))

        # 主操作（文字按钮）
        row_main = ctk.CTkFrame(bf, fg_color=C["bg"])
        row_main.pack(fill=tk.X, pady=(0, 6))
        flat_btn(row_main, "▶ 全部开始", self._start_all,
                 C["green"], C["green_hover"], font_size=11).pack(side=tk.LEFT, padx=(0, 8))
        flat_btn(row_main, "⏸ 全部暂停", self._pause_all,
                 C["orange"], C["orange_hover"], font_size=11).pack(side=tk.LEFT, padx=(0, 8))
        flat_btn(row_main, "🗄 清除已完成", self._archive_completed,
                 C["surface"], C["bg"], font_size=11, fg=C["text_ter"]).pack(side=tk.RIGHT)

        # 选中操作（纯图标 + tooltip）
        row_icon = ctk.CTkFrame(bf, fg_color=C["bg"])
        row_icon.pack(fill=tk.X)

        def sep(p):
            ctk.CTkFrame(p, fg_color=C["border"], width=1).pack(side=tk.LEFT, fill=tk.Y, padx=6, pady=2)

        _icon_btn(row_icon, "★", self._toggle_star_selected, "标重点").pack(side=tk.LEFT, padx=2)
        _icon_btn(row_icon, "✓", self._toggle_complete_selected, "标记完成").pack(side=tk.LEFT, padx=2)
        _icon_btn(row_icon, "✎", self._edit_selected, "编辑").pack(side=tk.LEFT, padx=2)
        _icon_btn(row_icon, "📅", self._change_date, "改期").pack(side=tk.LEFT, padx=2)
        sep(row_icon)
        _icon_btn(row_icon, "↑", self._move_up_selected, "上移").pack(side=tk.LEFT, padx=2)
        _icon_btn(row_icon, "↓", self._move_down_selected, "下移").pack(side=tk.LEFT, padx=2)
        sep(row_icon)
        _icon_btn(row_icon, "🗑", self._delete_selected, "删除", danger=True).pack(side=tk.LEFT, padx=2)
```

- [ ] **Step 3: 运行验证（截断根治）**

Run: `cd tools/workspace && python main.py`
Expected: 工具栏所有按钮完整可见、无横向截断；主操作为文字按钮（开始/暂停绿橙、清除已完成弱化靠右），下方一排图标按钮分组；hover 图标按钮显示 tooltip；在 150% DPI 下同样无截断。

- [ ] **Step 4: 提交**

```bash
git add workspace/src/main_app.py
git commit -m "feat(ui): 工具栏重设计（主操作文字 + 选中操作图标栏，根治截断）"
```

---

## Task 6: 列表区迁移（标题 / 搜索框 / Canvas 滚动）

**Files:**
- Modify: `workspace/src/main_app.py:209-261`（`card_list / lh / lc / canvas / scrollbar` 段）

**Interfaces:**
- Produces: 现代样式的搜索 `CTkEntry` 与滚动容器；Canvas + scrollregion 逻辑保留（CLAUDE.md 易错点）。

- [ ] **Step 1: 列表容器与标题**

约 209-218 行：`card_list / lh` 容器→`CTkFrame(fg_color=C["surface"])`；`list_title_lbl`→`CTkLabel`。

- [ ] **Step 2: 搜索框迁移**

约 223-228 行 `self.search_entry` 由 `tk.Entry` 改为：

```python
        self.search_entry = ctk.CTkEntry(lh, textvariable=self.search_var,
                                         fg_color=C["bg"], text_color=C["text"],
                                         border_color=C["border"], border_width=1,
                                         corner_radius=RADIUS["button"],
                                         font=(FONT, 10), width=160)
        self.search_entry.pack(side=tk.RIGHT)
```

- [ ] **Step 3: Canvas / 滚动区保留 + 样式**

约 233-254 行：`lc` 容器→`CTkFrame(fg_color=C["surface"])`；`tk.Canvas` 与 `tk.Scrollbar` **保留**（Canvas 内嵌 Frame 的 `scrollregion` 更新、`bind_all` 滚轮坐标守卫逻辑全部不变，见 CLAUDE.md 易错点），仅把 `bg=C["surface"]` 保持、`highlightthickness=0` 保持。`card_frame` 容器可保持 `tk.Frame`（Canvas 子窗口用 tk.Frame 兼容性更好）。

- [ ] **Step 4: 运行验证**

Run: `cd tools/workspace && python main.py`
Expected: 列表标题、搜索框为现代样式；卡片列表可正常滚动；滚轮在卡片上也能滚（坐标守卫未串扰计时面板）。

- [ ] **Step 5: 提交**

```bash
git add workspace/src/main_app.py
git commit -m "feat(ui): 列表区迁移 CTk（标题/搜索框/滚动容器）"
```

---

## Task 7: todo_card.py 迁移（圆角卡 / 区块头 / 空提示）

**Files:**
- Modify: `workspace/src/todo_card.py`（`TodoCard / SectionHeader / EmptyHint`）

**Interfaces:**
- Produces: `TodoCard` 为圆角 `CTkFrame`；左侧色条、① 序号、★ 重点、●○✓ 状态、文字、时间、⏰ 倒计时语义不变；`set_selected / _on_enter / _on_leave` 改为切换 `fg_color`。

- [ ] **Step 1: 导入与 TodoCard 容器**

`todo_card.py` 顶部导入改为 `import customtkinter as ctk`（保留 `import tkinter as tk` 备用），`from .constants import C, FONT, RADIUS`。`TodoCard.__init__` 的 `super().__init__` 由 `tk.Frame` 改为：

```python
        super().__init__(parent, fg_color=C["surface"], corner_radius=RADIUS["card"],
                         cursor="hand2", **kw)
```

> CTkFrame 无 `highlightthickness/bd`；选中态外框改用 `fg_color`（见 Step 3）。拖拽高亮 `set_dragging` 改为切换 `border_width` + `border_color`（见 Step 4）。

- [ ] **Step 2: 子控件 Label → CTkLabel**

把卡片内 `tk.Label(...)` 全部改为 `ctk.CTkLabel(...)`，参数映射：`bg=`→`fg_color=`、`fg=`→`text_color=`、`font=` 保持、`anchor=` 保持。左侧 `accent` 色条与内容 `tk.Frame` 容器改为 `ctk.CTkFrame(fg_color=C["surface"])`（色条 `fg_color=accent_color`，`width=3` + `pack_propagate(False)` 保留）。

> 易错：CTkLabel 默认有自身 padding，时间 Label 若需右对齐保持 `anchor="e"`；序号/状态用 `width=1` 的旧写法改为不设 width 或设 `width=20`，按视觉效果微调。

- [ ] **Step 3: 选中/悬停态改为 fg_color 切换**

`set_selected / _on_enter / _on_leave`（原约 157-257 行）原先递归比对 `cget("bg")` 改子控件底色——CTk 下改为只切卡片自身 `fg_color`：

```python
    def set_selected(self, selected):
        self._selected = selected
        self.configure(fg_color=C["blue_light"] if selected else C["surface"])

    def _on_enter(self, event):
        if event.widget is not self or self._selected or self._drag_active:
            return
        self.configure(fg_color=C["bg"])

    def _on_leave(self, event):
        if event.widget is not self or self._selected or self._drag_active:
            return
        self.configure(fg_color=C["surface"])
```

> 子控件不再单独改色（CTkLabel 透明继承卡片底色即可）；移除原 `_all_children` 递归改色循环。

- [ ] **Step 4: 拖拽高亮改为边框**

`set_dragging`（原约 204-209 行）：

```python
    def set_dragging(self, dragging):
        if dragging:
            self.configure(border_width=2, border_color=C["blue"])
        else:
            self.configure(border_width=0, border_color=C["surface"])
```

- [ ] **Step 5: SectionHeader / EmptyHint 迁移**

两者 `tk.Frame`→`ctk.CTkFrame(fg_color=C["surface"], corner_radius=0)`，内部 `tk.Label`→`ctk.CTkLabel`、分隔 `tk.Frame`→`ctk.CTkFrame(fg_color=C["border"])`。`refresh / update_reminder` 内 `configure(bg=...)` 改为 `configure(fg_color=...)`、`fg=`→`text_color=`。

- [ ] **Step 6: 运行验证**

Run: `cd tools/workspace && python main.py`
Expected: 卡片为圆角、左色条颜色正确（进行蓝/完成绿/重点橙/普通灰）；选中浅蓝、hover 浅灰；序号/状态/⏰ 倒计时正常显示；拖拽时卡片有蓝色边框。

- [ ] **Step 7: 提交**

```bash
git add workspace/src/todo_card.py
git commit -m "feat(ui): TodoCard/SectionHeader/EmptyHint 迁移 CTk 圆角卡"
```

---

## Task 8: 对话框迁移（日期选择 + 编辑待办）

**Files:**
- Modify: `workspace/src/date_picker.py`
- Modify: `workspace/src/main_app.py:836-867`（`_edit_selected` 内编辑对话框段，行号以 grep 为准）

**Interfaces:**
- 产出的对话框为 `CTkToplevel`；按钮经 Task 3 的 `flat_btn` 已是 CTkButton。

- [ ] **Step 1: date_picker.py 迁移**

`self.dlg = tk.Toplevel(parent)`→`self.dlg = ctk.CTkToplevel(parent)`；`self.dlg.configure(bg=...)`→`fg_color=C["surface"]`；内部 `tk.Frame/Label`→`CTkFrame/CTkLabel`（`‹/›` 导航与日期标签参数映射同 Task 4）。`确定/取消` 按钮已是 `flat_btn`，保持。

- [ ] **Step 2: 编辑对话框迁移**

`main_app.py` 编辑待办对话框（约 838-867 行）：`tk.Toplevel`→`ctk.CTkToplevel`，`bg=`→`fg_color=`，`tk.Label`→`CTkLabel`，`tk.Entry`→`ctk.CTkEntry`（参数映射同 Task 4 输入框），按钮已是 `flat_btn`/可改 `flat_btn`。

- [ ] **Step 3: 运行验证**

Run: `cd tools/workspace && python main.py`，触发：双击卡片编辑、点"改期"。
Expected: 两个对话框为现代圆角样式，按钮、输入框、日期导航正常工作，确定/取消有效。

- [ ] **Step 4: 提交**

```bash
git add workspace/src/date_picker.py workspace/src/main_app.py
git commit -m "feat(ui): 日期/编辑对话框迁移 CTkToplevel"
```

---

## Task 9: tooltip.py 迁移

**Files:**
- Modify: `workspace/src/tooltip.py`

**Interfaces:**
- 保留 `ToolTip(widget, text_fn)` 公开签名（Task 5 的图标按钮依赖它）。

- [ ] **Step 1: 迁移 tip 窗口**

`tooltip.py` 内 `self.tip = tk.Toplevel(self.widget)`→`self.tip = ctk.CTkToplevel(self.widget)`；`overrideredirect(True)`、`attributes("-topmost", True)` 保留。提示标签由 `tk.Label`→`ctk.CTkLabel`（`fg_color="#1F2329"`, `text_color="#FFFFFF"`, `corner_radius=6`, `font=(FONT,10)`, `padx/pady`→CTkLabel 用 `padx/pady` 参数或外层 pack padding）。定位 `geometry(f"+{x}+{y}")` 逻辑不变。

> 备选：若 `CTkToplevel` 在某些环境对 `overrideredirect` 表现不稳，保留 `tk.Toplevel` 亦可（tooltip 是独立浮层，混用无妨）。任选其一并保证可显示。

- [ ] **Step 2: 运行验证**

Run: `cd tools/workspace && python main.py`，鼠标悬停长文字卡片与工具栏图标按钮。
Expected: 500ms 后出现深底白字圆角提示，位置正确、不超出屏幕右侧。

- [ ] **Step 3: 提交**

```bash
git add workspace/src/tooltip.py
git commit -m "feat(ui): tooltip 迁移 CTk（深底白字圆角提示）"
```

---

## Task 10: 拖拽虚影 + 落点指示线迁移

**Files:**
- Modify: `workspace/src/main_app.py:996-1013`（`_drag_ghost` 创建段，行号以 grep 为准）及其它 `_drop_ind` 相关临时 Toplevel 创建处。

**Interfaces:**
- 虚影/指示线为临时 `CTkToplevel`；`overrideredirect + topmost + alpha` 保留。

- [ ] **Step 1: 虚影窗口迁移**

约 998-1009 行：`g = tk.Toplevel(...)`→`g = ctk.CTkToplevel(...)`；`g.overrideredirect(True)`、`g.attributes("-topmost", True)`、`g.attributes("-alpha", 0.85)` 保留；`g.configure(bg=C["blue"], ...)`→`g.configure(fg_color=C["blue"])`；内部 `tk.Frame/Label`→`CTkFrame/CTkLabel`。定位逻辑不变。

- [ ] **Step 2: 落点指示线迁移**

全局搜索 `_drop_ind` 与创建落点线的 `tk.Toplevel`（grep `overrideredirect` 在 main_app.py 内的所有命中），逐个同样替换为 `CTkToplevel`，背景色 `bg=`→`fg_color=`。

- [ ] **Step 3: 运行验证**

Run: `cd tools/workspace && python main.py`，拖拽一个卡片。
Expected: 拖拽时出现半透明虚影跟随光标；目标位置出现蓝色落点指示线；松开后在同组内重排、跨组弹回；释放后高亮清理。

- [ ] **Step 4: 提交**

```bash
git add workspace/src/main_app.py
git commit -m "feat(ui): 拖拽虚影/落点指示线迁移 CTkToplevel"
```

---

## Task 11: timer_panel.py 迁移（保留顶部吸附）

**Files:**
- Modify: `workspace/src/timer_panel.py`（`TimerPanel` 与 `TimerRow`）

**Interfaces:**
- 保留：吸附状态机（`_enter_dock/_exit_dock/_expand/_collapse`）、`_animate_to` 动画、`_check_leave` 坐标守卫、`DOCK_*` 常量、`bind_all` 滚轮坐标守卫、Canvas scrollregion、Observer `add_callback/remove_callback` 精确移除。仅控件换 CTk。

- [ ] **Step 1: 面板 Toplevel → CTkToplevel**

约 35-49 行：`self.win = tk.Toplevel(master_root)`→`self.win = ctk.CTkToplevel(master_root)`；`overrideredirect(True)`、`attributes("-topmost", True)`、`geometry("420x480")`、`withdraw()` 保留。`shadow`/`card` 由 `tk.Frame`→`ctk.CTkFrame`：`shadow` 用 `fg_color=C["shadow"]`，`card` 用 `fg_color=C["surface"], corner_radius=RADIUS["card"]`（去掉手贴假阴影，改用一层 `shadow` 边距 + card 圆角呈现层次）。

- [ ] **Step 2: 标题栏与内容控件迁移**

约 52-108 行：`title_bar`→`CTkFrame(fg_color=C["bg"])`；`⏱ 计时面板` 标题与 `✕` 关闭按钮→`CTkLabel`（`✕` 保留 `<Button-1>/<Enter>/<Leave>` 绑定）；分隔线 `CTkFrame(fg_color=C["border"])`；空提示 `CTkLabel`。`Canvas/Scrollbar/inner_frame`（约 78-108 行）**保留**（scrollregion、`bind_all` 滚轮坐标守卫不变），仅背景色映射。

- [ ] **Step 3: TimerRow 迁移**

`TimerRow` 继承 `ctk.CTkFrame(fg_color=C["surface"], corner_radius=RADIUS["card"])`；`accent` 色条与内容容器→`CTkFrame`；所有 `tk.Label`→`CTkLabel`（时间 `font=("Consolas",18,"bold")` 保留）；`_mini_btn` 内部把 `tk.Frame+tk.Label` 合成改为返回 `ctk.CTkButton`（`fg_color=C["surface"]`, `hover_color=C["blue_light"]`, `text_color` 跟随启用态, `corner_radius=RADIUS["button"]`, `width=28`）。`_btn_state` 改为 `btn_frame.configure(text_color=...)`（CTkButton 直接有 text_color）或对其内部标签设色——按 CTkButton 直接配置即可。`refresh/_status_icon/_accent_color/_time_color` 逻辑不变，仅 `configure(bg=)`→`configure(fg_color=)`、`fg=`→`text_color=`。右键菜单 `tk.Menu` 保留（原生，不在迁移范围）。

- [ ] **Step 4: 运行验证（吸附 + 计时）**

Run: `cd tools/workspace && python main.py`，双击卡片加入面板，计时、拖到顶部。
Expected: 面板为圆角现代样式；开始/暂停/重置/移除按钮正常；计时 `sleep≥1.2s` 后秒数正确递增；拖到屏幕顶部≤60px 自动吸附收起、鼠标进入展开、离开 500ms 收起；吸附/动画/坐标守卫均正常；移除待办后无回调泄漏（全停后面板 tick 自停）。

- [ ] **Step 5: 提交**

```bash
git add workspace/src/timer_panel.py
git commit -m "feat(ui): 计时面板迁移 CTk（圆角 + 保留顶部吸附/动画/坐标守卫）"
```

---

## Task 12: 打包 + 最终全量验证

**Files:**
- Modify: 打包命令（CLAUDE.md）新增 `--collect-all customtkinter`

- [ ] **Step 1: 更新打包命令**

按 spec §4，打包命令为：

```bash
cd tools/workspace && pyinstaller --onefile --noconsole --name "待办计时器" \
  --collect-all pystray --collect-all customtkinter --hidden-import six.moves \
  --distpath "../" --workpath "../build_tmp" main.py && \
rm -rf ../build_tmp 待办计时器.spec
```

同步更新 `CLAUDE.md`「命令」段的打包行（加 `--collect-all customtkinter`）。

- [ ] **Step 2: 执行打包**

Run 上述命令。
Expected: 生成 `tools/待办计时器.exe`，无缺失导入警告（特别检查 customtkinter 资源被 collect）。

- [ ] **Step 3: EXE 全量验证（对照 spec §9 清单）**

运行 `tools/待办计时器.exe`，逐项核对：
- [ ] 720×780 打开，所有按钮可见无截断（100%/125%/150% 各测）。
- [ ] 圆角/阴影/现代滚动条观感符合 `docs/mockup-main-window.html`。
- [ ] 计时准确（`sleep≥1.2s`）。
- [ ] 计时面板吸附/展开/拖拽/坐标守卫正常。
- [ ] 拖拽排序/落点线/5px 阈值点击正常。
- [ ] 托盘最小化/恢复/退出、开机自启正常。
- [ ] 原子保存/`.bak` 回退/运行恢复正常。
- [ ] `remove_callback` 精确移除（无泄漏）。
- [ ] EXE 在高 DPI 真机无模糊/无双重缩放。

- [ ] **Step 4: 提交打包配置**

```bash
git add CLAUDE.md
git commit -m "build(ui): 打包命令新增 --collect-all customtkinter + 全量验证通过"
```

---

## Self-Review

**1. Spec 覆盖**：§3 范围→Task 1-11 逐文件；§4 打包→Task 12；§5 视觉系统→constants(Task1)+各迁移任务；§6 布局（720×780→Task2，工具栏截断→Task5，卡片→Task7）；§7 逐组件→Task 2-11；§8 风险（DPI→Task2 Step4，吸附→Task11，滚轮/scrollregion→Task6/11）；§9 验证→Task12 + 各任务运行步。无遗漏。

**2. 占位符扫描**：无 TBD/TODO；机械迁移步骤给出具体 before→after 模式与代码块；DPI 给出主方案 + 回退方案。

**3. 类型/命名一致**：`RADIUS`（Task1 定义）被 Task 3/4/5/7/8/11 一致引用；`flat_btn` 签名（Task3）被 Task 4/5/8 调用点保持一致；`self.filter_seg`（Task4 定义）在 Task4 Step4 自洽；`ToolTip(widget, text_fn)` 签名（Task9）被 Task5 `_icon_btn` 一致使用。
