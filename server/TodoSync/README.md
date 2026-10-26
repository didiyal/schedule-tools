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

## 服务器访问

```bash
ssh -p 2222 -i ~/.ssh/shiwujie_server root@47.112.114.139   # 必须走 2222（本机封出站 22）
```

## 已踩的坑（重要）

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

```bash
cd server/TodoSync
export DOTNET_ROOT=D:/dotnet-sdk
dotnet publish TodoSync.csproj -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:DebugType=none
scp -P 2222 -i ~/.ssh/shiwujie_server \
  bin/Release/net8.0/linux-x64/publish/TodoSync \
  bin/Release/net8.0/linux-x64/publish/libe_sqlite3.so \
  root@47.112.114.139:/www/wwwroot/todosync/
ssh -p 2222 -i ~/.ssh/shiwujie_server root@47.112.114.139 \
  'chmod +x /www/wwwroot/todosync/TodoSync && chown -R www:www /www/wwwroot/todosync && systemctl restart todosync && sleep 1 && curl -s http://127.0.0.1:5000/healthz'
```

数据在 data/ 目录，重发不清数据；证书 10 年有效无需续期。
