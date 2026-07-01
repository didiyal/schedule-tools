# 版本变更记录 — v1.1

待办计时器 v1.1 在 v1.0 功能基础上完成 **UI 现代化**（迁移 customtkinter）与**正式安装器分发**。功能行为不变，仅视觉与分发方式升级。

## 新增

- **UI 现代化**：整体迁移至 customtkinter，建立飞书风格配色 + 圆角卡片化视觉系统（新增 `RADIUS` 圆角 token）。
  - 根窗口、工具栏、列表区、卡片、日期/编辑对话框、tooltip、拖拽虚影、计时面板全部迁移 CTk 圆角风格。
  - 工具栏重设计：主操作文字 + 选中操作图标栏，根治文字截断。
- **顶部版本号**：标题右侧显示 `v1.1.0`（来源 `constants.APP_VERSION`）。
- **Inno Setup 安装器**（`installer/`）：
  - 单用户免管理员，默认装 `%LOCALAPPDATA%\Programs\待办计时器`，向导可选安装路径。
  - 固定 `AppId` → 重复运行原地覆盖升级（升级前 `taskkill` 释放 EXE 占用）。
  - `data/` 不打入安装包 → 升级保留用户数据；卸载弹窗可选删除本地数据。
  - 简体中文向导（随包 `ChineseSimplified.isl`，kira-96 译，UTF-8 BOM）。
  - 蓝底白色钟表应用图标（PIL 生成，多尺寸）。
- **一键打包脚本** `installer/build_installer.bat`：图标 → PyInstaller → iscc 串联；`installer/make_icon.py` 单独重生成图标。

## 优化

- 窗口与卡片尺寸收紧、字号统一调整；顶栏紧凑上移、卡片间加分界。
- 计时悬浮面板按钮重设计：实心彩色按钮（▶绿 ⏸橙 ↺灰 ✕红 + 白图标），白卡上高对比可见；后缩小至 28×24。
- 三态视图切换预加载：按 `todo_id` 缓存复用卡片，切换不再重建控件（性能）。
- 修复空 `CTkFrame` 默认高/宽膨胀（~250px / ~200px 不收缩）导致的卡片过高与按钮溢出。

## 技术

- 依赖新增 `customtkinter`；打包命令新增 `--collect-all customtkinter`。
- DPI 单一缩放源：customtkinter 统一接管，不再手动 `SetProcessDpiAwareness`（避免双重缩放）。
- 版本号单源 `constants.APP_VERSION`，与 `installer/installer.iss` 的 `MyAppVersion`、`build_installer.bat` 输出名同源。
- 安装器产物约 31 MB（含 Python 运行时 + 全依赖）；裸 EXE 仍可单独分发（无安装路径/升级/卸载删数据能力）。
