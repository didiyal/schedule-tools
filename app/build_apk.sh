#!/usr/bin/env bash
# TodoTimer APP 一键构建（Windows/Git Bash）
# 坑位处理（都是实测踩过的）：
#   1. 项目源码路径含中文（tools/app 在 D:\桌面\ 下）：
#      · AGP 拒绝构建 → android/gradle.properties 需 android.overridePathCheck=true
#      · impellerc（着色器）与 gen_snapshot（release AOT）写/读中文路径直接失败
#        → release 必须把整个项目复制到 ASCII 路径（D:/Development/appbuild/src）构建
#   2. Gradle 构建产物也重定向到 D:/Development/appbuild（android/build.gradle.kts）
#      → flutter 工具按 <project>/build 找 APK 会"找不到"，构建其实已完成，脚本负责复制回来
#   3. Kotlin daemon 崩溃 → gradle.properties: kotlin.compiler.execution.strategy=in-process
#   4. 国内网络 → pub/gradle 全走阿里云/腾讯镜像（见 build.gradle.kts 与 gradle-wrapper.properties）
# 用法：bash build_apk.sh [debug|release]
set -euo pipefail
cd "$(dirname "$0")"
export PUB_HOSTED_URL=https://pub.flutter-io.cn
export FLUTTER_STORAGE_BASE_URL=https://storage.flutter-io.cn
export ANDROID_HOME="C:/Users/Administrator/AppData/Local/Android/Sdk"
export JAVA_HOME="D:/Development/JDK/JDK 17.0.5"
FLUTTER="D:/Development/flutter/bin/flutter.bat"
MODE=${1:-debug}   # debug | release
BUILD_ROOT=/d/Development/appbuild

BUILD_MARK=$(date +%s)

if [ "$MODE" = "release" ]; then
  # release 的 Dart AOT（gen_snapshot）吃不了中文路径：整项目复制到 ASCII 路径构建
  rm -rf "$BUILD_ROOT/src/app"
  mkdir -p "$BUILD_ROOT/src"
  cp -r . "$BUILD_ROOT/src/app"
  rm -rf "$BUILD_ROOT/src/app/build" "$BUILD_ROOT/src/app/.dart_tool"
  (cd "$BUILD_ROOT/src/app" && "$FLUTTER" build apk --release --suppress-analytics < /dev/null) || true
  SRC="$BUILD_ROOT/build/app/outputs/flutter-apk/app-release.apk"
else
  "$FLUTTER" build apk --debug --suppress-analytics < /dev/null || true
  SRC="$BUILD_ROOT/build/app/outputs/flutter-apk/app-debug.apk"
fi

DST="../build_tmp/TodoTimer-$MODE.apk"
mkdir -p ../build_tmp

# 防陈旧包上传（2026-10-28 实测：Gradle 真失败时旧 APK 仍在原位，脚本会拿旧包当新包传）——
# 构建后 SRC 未刷新（mtime 早于构建开始）则报错退出，不覆盖本地产物、不上服务器
if [ ! -f "$SRC" ] || [ "$(stat -c %Y "$SRC")" -lt "$BUILD_MARK" ]; then
  echo "[BUILD][ERROR] 未产出新 APK（Gradle 真失败），保留旧产物不上传" >&2
  exit 1
fi
cp -f "$SRC" "$DST"
echo "APK => $(realpath "$DST")"

# 5) release 自动上服务器（2026-10-28 用户指令）：scp 到 /api/app/latest 分发路径，
#    手机端 30s 内出更新横幅。失败只警告不阻断（可手动按 server/TodoSync/README.md「APP 更新包发布」补传）。
if [ "$MODE" = "release" ] && [ -f "$DST" ]; then
  if scp -P 2222 -i ~/.ssh/shiwujie_server -o ConnectTimeout=10 "$DST" root@47.112.114.139:/tmp/tt.apk 2>/dev/null; then
    ssh -p 2222 -i ~/.ssh/shiwujie_server -o ConnectTimeout=10 root@47.112.114.139 '
      mkdir -p /www/wwwroot/todosync/data/apk &&
      mv -f /tmp/tt.apk /www/wwwroot/todosync/data/apk/todo-timer.apk &&
      chown www:www /www/wwwroot/todosync/data/apk/todo-timer.apk'       && echo "[upload] APK 已上服务器，手机端将收到更新横幅"       || echo "[upload][WARN] 服务器端 mv 失败，手动补传"
  else
    echo "[upload][WARN] scp 失败（服务器不可达？），APK 仅存本地 $DST"
  fi
fi
