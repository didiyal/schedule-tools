#!/usr/bin/env bash
# 在服务器上执行：签自签证书 → 装 443 站点配置 → nginx 校验并热加载 → 本机 https 冒烟
set -euo pipefail
mkdir -p /www/server/nginx/conf/cert
if [ ! -f /www/server/nginx/conf/cert/todosync.crt ]; then
  openssl req -x509 -newkey rsa:2048 -nodes -days 3650 \
    -keyout /www/server/nginx/conf/cert/todosync.key \
    -out /www/server/nginx/conf/cert/todosync.crt \
    -subj "/CN=47.112.114.139" \
    -addext "subjectAltName=IP:47.112.114.139"
  chmod 600 /www/server/nginx/conf/cert/todosync.key
  echo "CERT: generated"
else
  echo "CERT: exists, kept"
fi
cp -f /tmp/todosync-443.conf /www/server/panel/vhost/nginx/todosync-443.conf
/www/server/nginx/sbin/nginx -t
/www/server/nginx/sbin/nginx -s reload
sleep 1
echo "--- https local smoke:"
curl -sk https://127.0.0.1/healthz
echo
echo "--- fingerprint (sha256, 供客户端钉证书):"
openssl x509 -in /www/server/nginx/conf/cert/todosync.crt -noout -fingerprint -sha256
