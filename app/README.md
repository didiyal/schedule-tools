# 待办计时器 安卓端（v1.5 S3）

Flutter APP，对接 [TodoSync 服务端](../server/TodoSync/README.md)实现任务云同步。
MVP 范围：任务树浏览（日期倒序日组 + 3 层树）、勾选完成、新建任务/子任务、
删除（墓碑同步到全端）、开始/暂停计时、只看今日、下拉刷新。

## 构建

```bash
cd app
bash build_apk.sh debug    # 产物 build_tmp/TodoTimer-debug.apk
bash build_apk.sh release  # 产物 build_tmp/TodoTimer-release.apk（AOT, ~47MB）
```

前置：Flutter SDK（`D:/Development/flutter`，3.35.4 stable）、Android SDK
（`%LOCALAPPDATA%/Android/Sdk`，platform 34/36）、JDK 17（`D:/Development/JDK/JDK 17.0.5`）。
环境变量与镜像都写死在脚本里；构建细节坑位见脚本头部注释（中文路径三连坑）。

## 架构

```
lib/
  api.dart     HTTP 客户端：TLS 自签证书钉扎（SHA-256 指纹硬编码，
               不匹配即拒——安全边界，勿用"信任一切"兜底）
  todo.dart    TodoItem 契约（与桌面/服务端逐字段对齐，snake_case）+ 本地 JSON 持久化
  engine.dart  同步引擎：待推队列（失败重试）+ 增量拉取合并（远端 LWW 大者胜）+
               本地编辑即改 updatedAt 立即上行
  main.dart    UI（Material 3）：30s 心跳轮询 + 回前台即同步 + 下拉刷新
```

## 数据流

- 启动：读本地 `todos.json` → 有配置则连接（`/healthz` 校验+钉证书）→ 30s 轮询
- 本地改：updatedAt=now → 存本地 → 立即上行（失败进待推队列，下轮重试）
- 远端改：`GET /api/changes?since=N` 增量 → LWW 合并 → 落盘重渲染
- 删除：本地移除 + 墓碑上行（与桌面/服务端三端一致）

## 配对/配置

设置页（齿轮）粘贴服务器地址 + **设备令牌**。设备令牌两种来源：
1. 桌面端（权威端）后续提供配对码换取（服务端 API 已有 `/api/pair`）；
2. 直接读服务器 `/www/wwwroot/todosync/data/admin.token`（当前 APP 与桌面共用管理员令牌，单用户够用）。

## 已知限制（MVP）

- 不做：计时面板独立窗、托盘、归档管理、任务拖拽排序（树操作在桌面端完成）
- 完成态节点在手机上折叠子树（简报视图）
- 令牌明文存 SharedPreferences（单用户自用；如需加固用 flutter_secure_storage）
