# TodoSync 部署手册（阿里云深圳轻量 47.112.114.139）

> 2026-10-26 已完成首次部署。更新服务端代码后的重发流程见文末。

## 线上形态

- 服务：systemd 单元 `todosync`（`/etc/systemd/system/todosync.service`），
  二进制 `/www/wwwroot/TodoSync 实际路径=/www/wwwroot/todosync/TodoSync`
  （linux-x64 self-contained 单文件，无需安装 .NET），以 `www` 用户运行，
  监听 `127.0.0.1:5000`（Kestrel）。
- 数据：`/www/wwwroot/todosync/data/`——`todos.db`（SQLite WAL）+
  `admin.token`（管理员令牌，桌面端"权威端"粘贴一次）。
  **数据目录严禁入库/外泄**。
- 入口：宝塔 Nginx 443 → 反代 5000。站点配置
  `/www/server/panel/vhost/nginx/todosync-443.conf`，自签证书
  `/www/server/nginx/conf/cert/todosync.{crt,key}`（SAN=IP，2026-10-26 签，10 年）。
- **证书指纹（客户端钉证书用）**：
  `BC:68:BF:4F:6F:1E:9E:0A:63:61:B2:93:57:8C:39:DC:7D:E6:C4:C6:CF:9B:65:0A:4B:E9:14:1A:04:3E:03:62`
- SWAS 防火墙 443/tcp 已放行（ListFirewallRules 原有规则，未新增）。

- **空间隔离（v1.5 多用户，2026-10-27）**：todos/devices 带 space_id；管理员令牌
  =default 空间；为新用户开空间=管理员调 paircode 传 {"space":"用户名"} → 对方
  配对即入该空间（数据互不可见）；`GET /api/admin/spaces` 查空间分布。
  共享清单（一空间多用户共管）为后续方向。API 面已扩：
  /healthz（含 app 字段）、POST /api/pair、POST|GET /api/changes、
  POST /api/admin/paircode（可带 space）、GET /api/admin/spaces、/api/admin/devices。

## 服务器访问

```bash
ssh -p 2222 -i ~/.ssh/shiwujie_server root@47.112.114.139   # 必须走 2222（本机封出站 22）
```

## 已踩的坑（重要）

0. **同端口多站点抢占（2026-10-27 事故）**：新站点 `oscg-443.conf`（server_name
   oscg.local）与本项目同抢 443；IP 直连无 SNI 时 nginx 按**字典序**把请求发给
   第一个 server 块 → oscg 截走全部流量，TodoSync 公网 404、手机钉扎证书失败。
   修法：todosync-443.conf 标记 `listen 443 ssl default_server;`（无 SNI 兜底）。
   **以后任何新 443 站点都不许再设 default_server**，且上线前必须
   `curl -sk https://47.112.114.139/healthz` 验证回归。
1. **BT 全局 `proxy_cache cache_one`（proxy.conf）会损坏无 Content-Length 的
   POST**——经 443 反代的无 body POST 返回**双重 400 空响应头**（nginx 本体生成，
   不达上游）。修法：todosync-443.conf 的 location 里 `proxy_cache off` +
   `proxy_http_version 1.1`。改任何反代配置后必须 `nginx -t && nginx -s reload`。
2. 该机器 nginx 带 lua/cache_purge 等模块（宝塔 9.2.0 编译），排错先区分
   400 是 nginx 本体（`Server: nginx` + 空体）还是应用 JSON。
3. 同机跑着生产 `spring_shiwujieBootstrap`（8100）与 80 静态站——
   **不许 pkill java、不许动其 EnvironmentFile 与站点配置**。

## 验收记录（2026-10-26）

- 服务器本机：`curl -sk https://127.0.0.1/healthz` → `{"ok":true,...}` ✓
- 公网（本机 Windows）：`curl -k https://47.112.114.139/healthz` ✓；无令牌 401 ✓
- 公网手机视角全流程：paircode → pair → push 2 条(中文) → pull 校验 UTF-8 ✓
- 冒烟后已重置 `todos.db`（干净初始态 version=0），`admin.token` 保留。
- 既有生产：`spring_shiwujieBootstrap` active、80 站点 200，未受影响 ✓

## 重发流程（改代码后）

> ⚠ 两个实测教训（2026-10-28）：
> ①**必须带 `-p:PublishSingleFile=true`**——漏了会发布成 apphost+DLL 多文件布局，
> 只传 TodoSync+libe_sqlite3.so 两个文件 → 服务起不来
>（`The application to execute does not exist: '.../TodoSync.dll'`），生产中断；
> ②服务运行中**不能直接 scp 覆盖 EXE**（ETXTBSY：dest open Failure）——
> 先传 /tmp 再 stop 后原子 mv。以下为修正后流程：

```bash
cd server/TodoSync
export DOTNET_ROOT=D:/dotnet-sdk
dotnet publish TodoSync.csproj -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:DebugType=none
scp -P 2222 -i ~/.ssh/shiwujie_server \
  bin/Release/net8.0/linux-x64/publish/TodoSync \
  bin/Release/net8.0/linux-x64/publish/libe_sqlite3.so \
  root@47.112.114.139:/tmp/
ssh -p 2222 -i ~/.ssh/shiwujie_server root@47.112.114.139 \
  'systemctl stop todosync && mv /tmp/TodoSync /www/wwwroot/todosync/TodoSync && mv /tmp/libe_sqlite3.so /www/wwwroot/todosync/libe_sqlite3.so && chmod +x /www/wwwroot/todosync/TodoSync && chown -R www:www /www/wwwroot/todosync && systemctl start todosync && sleep 1 && curl -s http://127.0.0.1:5000/healthz'
# 红线回归：curl -sk https://47.112.114.139/healthz
```

数据在 data/ 目录，重发不清数据；证书 10 年有效无需续期。

## APP 更新包发布（v1.4.2 起每版必做）

服务端 `GET /api/app/latest`（免鉴权）下发 `data/apk/todo-timer.apk`；
healthz 的 `app` 字段 = 最新客户端版本号（`Util.cs` 的 `AppVersion`）。
APP 连接时比对版本，有新版就在界面顶部出「点此更新」横幅，
钉扎 TLS 下载后拉起系统安装器。发布步骤：

1. 构建 release APK：`cd app && bash build_apk.sh release`（产物 `tools/build_tmp/TodoTimer-release.apk`）
2. 版本号对齐：`app/pubspec.yaml` version ↔ `server/TodoSync/Util.cs` AppVersion
3. APK 上服务端：
   `scp -P 2222 -i ~/.ssh/shiwujie_server ../../build_tmp/TodoTimer-release.apk root@47.112.114.139:/tmp/tt.apk`
   再 ssh：`mv /tmp/tt.apk /www/wwwroot/todosync/data/apk/todo-timer.apk && chown www:www /www/wwwroot/todosync/data/apk/todo-timer.apk`
4. 若 Util.AppVersion 变了 → 走上面「重发流程」重发服务端
5. 冒烟：healthz 的 app = 新版本；latest 下载字节数与本地 APK 一致
   （`curl -sk https://47.112.114.139/api/app/latest -o /tmp/dl.apk && ls -la /tmp/dl.apk`）

注意：healthz `app` 只升不降——它同时驱动桌面端更新提示（桌面 1.4.1 起只在
服务器版本更新时提示，旧服务器不会误报）。
