# 发布检查清单 — v1.0

## 构建

- [ ] 工作区干净：`git status` 无未提交改动。
- [ ] 开发模式可运行：`cd workspace && python main.py` 无异常。
- [ ] 依赖齐全：Python 3.10+、pystray、pillow。

## 打包

- [ ] 执行打包命令（见 `CLAUDE.md` / `README.md`）：
      `pyinstaller --onefile --noconsole --name "待办计时器" --collect-all pystray --hidden-import six.moves --distpath "../" --workpath "../build_tmp" main.py`
- [ ] 清理临时产物：`rm -rf ../build_tmp 待办计时器.spec`
- [ ] 产物 `待办计时器.exe` 约 12 MB，双击无控制台黑窗。

## 功能验证（打包版）

- [ ] 托盘、吸附、自启、滚轮在打包版均正常（`--collect-all pystray` 生效）。
- [ ] 按 [验收标准](../../product/v1.0/acceptance-criteria.md) AC-1 ~ AC-6 抽测关键路径。

## 发布

- [ ] 提交并推送：`git push`。
- [ ] 打标签：`git tag -a v1.0 -m "待办计时器 v1.0 首个正式版本"` 并 `git push origin v1.0`。
- [ ] 更新 `docs/` 下 `current.md` / `CHANGELOG.md` 指向 v1.0。
