import 'dart:io';

import 'package:open_filex/open_filex.dart';

import 'api.dart';

/// APP 自更新（2026-10-28 用户需求）：
/// 服务器 healthz 的 `app` 字段 = 最新客户端版本号（发布时与 Util.AppVersion 同步），
/// 发现更新 → APP 顶栏横幅「点此更新」→ 钉扎 TLS 下载 /api/app/latest →
/// 拉起系统安装器（首次需在系统弹窗里允许「安装未知应用」）。
class Updater {
  /// 服务器版本比本地新则返回服务器版本号，否则 null。
  /// 版本段比较（1.4.2 > 1.4.10）；任一侧解析失败宁可提示不漏更。
  static String? newerVersion(String? server, String local) {
    if (server == null || server.isEmpty || server == local) return null;
    final s = _parse(server), l = _parse(local);
    if (s == null || l == null) return server;
    return s[0] > l[0] || (s[0] == l[0] && s[1] > l[1]) ||
            (s[0] == l[0] && s[1] == l[1] && s[2] > l[2])
        ? server
        : null;
  }

  static List<int>? _parse(String v) {
    final m = RegExp(r'^(\d+)\.(\d+)(?:\.(\d+))?').firstMatch(v.trim());
    if (m == null) return null;
    return [int.parse(m.group(1)!), int.parse(m.group(2)!), int.parse(m.group(3) ?? '0')];
  }

  /// 下载更新包到应用文档目录并拉起系统安装器。
  static Future<void> downloadAndInstall(TodoApi api, String version, String docsPath,
      {void Function(int received, int total)? onProgress}) async {
    final dir = Directory('$docsPath/update');
    if (!await dir.exists()) await dir.create(recursive: true);
    final path = '${dir.path}/TodoTimer-$version.apk';
    await api.downloadApk(path, onProgress: onProgress);
    final r = await OpenFilex.open(path);
    if (r.type != ResultType.done) {
      throw Exception('打开安装包失败：${r.message}');
    }
  }
}
