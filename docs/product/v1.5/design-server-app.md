# v1.5 服务端 + 手机 APP 架构设计（2026-10-26 提案）

> 目标：手机 APP 可查看/控制任务内容；为此把纯本地的待办计时器升级为
> 「本地优先 + 云同步枢纽」架构。桌面端使用习惯完全不变（离线可用），
> 服务端只做同步枢纽，不做重业务。

## 现状（v1.4，纯本地）

```
[桌面 GUI(WPF)] ──同一进程──> todos.json（本地文件，原子写+备份）
[CLI(headless)] ──读写同一文件；GUI 在线时经 IPC 127.0.0.1:58372 通知刷新
```

无服务器、无数据库、无对外端口（IPC 仅回环）。

## 目标架构

```
[桌面 v1.5 WPF] ──push/pull(HTTPS)──┐
                                    │
                            [TodoSync Server]  ← 阿里云深圳轻量 47.112.114.139
                            ASP.NET Core 8 + SQLite + 设备令牌
                                    │
[手机 APP(Flutter)] ──HTTPS API─────┘
```

- 合并策略：单用户多端，按条目 `updatedAt` **last-writer-wins**（LWW）。
  删除用墓碑（`deletedAt`）防止删后复活。
- 桌面端保持本地优先：同步失败不影响任何本地功能；联网后自动补推/补拉。

## 服务端设计（TodoSync Server）

- **栈**：C# / ASP.NET Core 8 Minimal API（与桌面共用 TodoItem 契约语义，
  单语言栈；服务器 Linux，dotnet self-contained 发布）。
- **存储**：SQLite（WAL），单文件好备份；表 `todos`（含软删）+ `devices` +
  `meta(version)`。全局递增版本号，每条变更 bump。
- **鉴权**：单用户；桌面端设置页生成**设备令牌**（长随机串），手机扫码/粘贴
  配对；所有 API 走 HTTPS + `Authorization: Bearer <token>`。
- **API 草案**：
  - `POST /api/pair {pairCode}` → `{deviceToken}`（配对码由桌面端生成，5 分钟有效）
  - `GET  /api/todos` → 全量快照 `{version, todos[]}`
  - `POST /api/changes {items[]}` → 上行变更（按 id upsert，LWW）
  - `GET  /api/changes?since=N` → 下行增量 `{version, items[], deletedIds[]}`
  - `GET  /api/archive`（只读）
  - （二期）`GET /api/stream` SSE 推送，替代手机轮询
- **部署**（47.112.114.139，宝塔面板 Linux）：
  self-contained 发布 → systemd 常驻 → 宝塔 Nginx 反代 443 证书 → Kestrel 5000。
  需要域名签 Let's Encrypt（待确认域名；无域名则先用自签/HTTP+令牌过渡）。
  新增防火墙规则：仅保留 80/443 对外，Kestrel 只听 127.0.0.1。

## 桌面端改造（v1.5）

1. `TodoItem` 增加 `updatedAt`（秒级时间戳，缺失按 0 兼容旧数据）与墓碑语义
   （本地即时物理删，同步层维护 30 天墓碑表）。
2. 新增 `Services/SyncService`：挂 `ScheduleSave` 存盘钩子做推（防抖+失败退避）、
   定时拉增量合并；冲突 LWW；同步状态（最近同步时间/失败原因）入设置区。
3. IPC/CLI 不变——CLI 写盘后仍走既有链路，同步由存盘钩子统一触发。

## 手机 APP（MVP）

- **栈推荐 Flutter**（Android+iOS 单库、热重载、中文生态成熟）。
- MVP 范围：任务树浏览、增/删/改/勾选、开始/暂停计时、今日视图、时长查看。
  不做：面板、托盘、提醒、归档管理（服务端照常为桌面归档）。

## 安全前置整改（在上线服务端之前必做）

当前防火墙对 0.0.0.0/0 开放了 3306(MySQL)、6379(Redis)、8848/9848(Nacos)、
1883(MQTT) 及 `-1` 全端口——Redis/MySQL 公网裸奔是常见入侵入口。
整改：以上端口改限源 IP 或仅内网；`-1` 规则删除；Redis 确认有密码。

## 里程碑

- **S0** 安全整改（防火墙收紧）——半小时
- **S1** TodoSync Server 上线（SQLite + 配对 + 全量/增量 API + systemd + HTTPS）
- **S2** 桌面 v1.5 同步模块（推拉合并 + 设置 UI）
- **S3** Flutter APP MVP
- S1~S3 可并行推进；S2/S3 都依赖 S1。

## 待决问题

1. 是否有域名可解析到 47.112.114.139？（决定 HTTPS 方案）
2. 手机端确认 Flutter？
3. MySQL 服务器上已有——是否坚持 SQLite（推荐，够用）？
