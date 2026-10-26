#!/usr/bin/env bash
# TodoSync 端到端冒烟：临时数据目录起服务 → 健康 → 管理员建配对码 → 配对换令牌
#   → 推变更 → LWW 旧包被拒/新包被收 → 拉增量 → 墓碑 → 配对码一次性。
# 注意：带中文的 JSON payload 必须走 UTF-8 文件(--data-binary @file)，
#   命令行内联中文会被 MSYS 按 GBK 转码导致服务端解析失败（Windows curl 经典坑）。
set -euo pipefail
cd "$(dirname "$0")"
export DOTNET_ROOT="${DOTNET_ROOT:-D:/dotnet-sdk}"   # 便携 SDK：apphost 解析 hostfxr 必需
PORT=${PORT:-5199}
BASE="http://127.0.0.1:$PORT"
DATA=$(mktemp -d)
DOTNET="D:/dotnet-sdk/dotnet.exe"

"$DOTNET" build TodoSync.csproj -v q --nologo || { echo "BUILD-FAIL"; exit 1; }

"$DOTNET" bin/Debug/net8.0/TodoSync.dll --urls "http://127.0.0.1:$PORT" --data-dir "$DATA" &
SRV=$!
trap 'kill $SRV 2>/dev/null; taskkill //F //IM TodoSync.exe >/dev/null 2>&1' EXIT

for i in $(seq 1 40); do curl -sf "$BASE/healthz" >/dev/null 2>&1 && break; sleep 0.5; done
curl -sf "$BASE/healthz" >/dev/null || { echo "SERVER-NOT-UP"; exit 1; }
echo "healthz: ok"

ADMIN=$(tr -d '\r\n ' < "$DATA/admin.token")
[ -n "$ADMIN" ] || { echo "NO-ADMIN-TOKEN"; exit 1; }

# 1. 管理员生成配对码
CODE=$(curl -sf -X POST -H "Authorization: Bearer $ADMIN" "$BASE/api/admin/paircode" \
    | python -c "import json,sys;print(json.load(sys.stdin)['code'])")
[ -n "$CODE" ] || { echo "PAIRCODE-FAIL"; exit 1; }
echo "paircode: $CODE"

# 2. 错误配对码被拒
S1=$(curl -s -o /dev/null -w '%{http_code}' -X POST -H "Content-Type: application/json" \
    -d '{"code":"000000","name":"bad"}' "$BASE/api/pair")
[ "$S1" = "403" ] || { echo "BAD-CODE-NOT-REJECTED: $S1"; exit 1; }
echo "reject-bad-code: 403 ok"

# 3. 正确配对 → 设备令牌
TOKEN=$(curl -sf -X POST -H "Content-Type: application/json" \
    -d "{\"code\":\"$CODE\",\"name\":\"smoke-phone\"}" "$BASE/api/pair" \
    | python -c "import json,sys;print(json.load(sys.stdin)['token'])")
[ -n "$TOKEN" ] || { echo "PAIR-FAIL"; exit 1; }
echo "pair: token ok"

# 4. 无令牌访问被拒
S2=$(curl -s -o /dev/null -w '%{http_code}' "$BASE/api/changes?since=0")
[ "$S2" = "401" ] || { echo "NO-AUTH-NOT-REJECTED: $S2"; exit 1; }
echo "reject-no-token: 401 ok"

# 5. 推 2 条变更
curl -sf -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    --data-binary @smoke/push_new.json "$BASE/api/changes" \
    | python -c "import json,sys;d=json.load(sys.stdin);assert d['accepted']==2,d;print('push: accepted=2 ok, version='+str(d['version']))"

# 6. LWW：同条目更旧的更新被拒
curl -sf -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    --data-binary @smoke/push_old.json "$BASE/api/changes" \
    | python -c "import json,sys;d=json.load(sys.stdin);assert d['accepted']==0,d;print('lww-old-rejected: ok')"

# 7. 更新的更新被接受
curl -sf -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    --data-binary @smoke/push_update.json "$BASE/api/changes" \
    | python -c "import json,sys;d=json.load(sys.stdin);assert d['accepted']==1,d;print('lww-new-accepted: ok')"

# 8. 拉全量：2 条，a1 是新文本
curl -sf -H "Authorization: Bearer $TOKEN" "$BASE/api/changes?since=0" | python -c "
import json,sys
d=json.load(sys.stdin); m={i['id']:i for i in d['items']}
assert len(d['items'])==2, d
assert m['a1']['json']['text']=='任务A改', m['a1']
assert m['a1']['updatedAt']==200
print('pull-all: 2 items, LWW result ok')"

# 9. 墓碑：删除 a1（deletedAt）后增量可见
curl -sf -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    --data-binary @smoke/push_tomb.json "$BASE/api/changes" >/dev/null
curl -sf -H "Authorization: Bearer $TOKEN" "$BASE/api/changes?since=2" | python -c "
import json,sys
d=json.load(sys.stdin); m={i['id']:i for i in d['items']}
assert 'a1' in m and m['a1']['deletedAt']==300, m
print('tombstone-delta: ok')"

# 10. 配对码用后即焚
S3=$(curl -s -o /dev/null -w '%{http_code}' -X POST -H "Content-Type: application/json" \
    -d "{\"code\":\"$CODE\",\"name\":\"replay\"}" "$BASE/api/pair")
[ "$S3" = "403" ] || { echo "CODE-REPLAY-NOT-REJECTED: $S3"; exit 1; }
echo "paircode-one-shot: ok"

echo "SMOKE-OK"
