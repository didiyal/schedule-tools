import 'dart:convert';
import 'dart:io';

import 'package:crypto/crypto.dart';

/// 服务器 API 客户端（v1.5 S3）。
/// TLS：服务器为自签证书，[pinnedSha256] 非空时逐请求比对证书指纹（hex 冒号分隔），
/// 不匹配即抛 CertificateException——钉证书是安全边界，别用全局 allowBadCert 兜底。
class TodoApi {
  /// 服务器自签证书 SHA-256 指纹（部署时锁定，见 server/TodoSync/README.md）。
  /// 钉扎是安全边界：指纹变更（重装/换证）需同步更新各端。
  static const String defaultPinnedSha256 =
      'BC:68:BF:4F:6F:1E:9E:0A:63:61:B2:93:57:8C:39:DC:7D:E6:C4:C6:CF:9B:65:0A:4B:E9:14:1A:04:3E:03:62';

  TodoApi({required this.server, required this.token, required this.pinnedSha256})
      : _base = server.endsWith('/') ? server.substring(0, server.length - 1) : server;

  final String server;
  final String token;
  final String? pinnedSha256;
  final String _base;

  HttpClient? _client;

  HttpClient _http() => _client ??= HttpClient()
    ..connectionTimeout = const Duration(seconds: 15)
    ..badCertificateCallback = (cert, host, port) {
      final pin = pinnedSha256;
      if (pin == null || pin.isEmpty) return false;   // 未配置指纹绝不放行自签
      return _fp(sha256.convert(cert.der).bytes) == _normalize(pin);
    };

  /// 无冒号大写 hex（与 _normalize 输出同格式）。
  static String _fp(List<int> bytes) =>
      bytes.map((b) => b.toRadixString(16).padLeft(2, '0').toUpperCase()).join();

  static String _normalize(String s) =>
      s.replaceAll(' ', '').replaceAll(':', '').toUpperCase();

  Map<String, String> get _headers => {
        'Authorization': 'Bearer $token',
        'Content-Type': 'application/json; charset=utf-8',
      };

  Future<Map<String, dynamic>> _get(String path) async {
    final rq = await _http().getUrl(Uri.parse('$_base$path'));
    _headers.forEach(rq.headers.set);
    final rs = await rq.close().timeout(const Duration(seconds: 20));
    final body = await rs.transform(utf8.decoder).join();
    if (rs.statusCode != 200) throw ApiException(rs.statusCode, body);
    return jsonDecode(body) as Map<String, dynamic>;
  }

  Future<Map<String, dynamic>> _post(String path, Map<String, dynamic> body) async {
    final rq = await _http().postUrl(Uri.parse('$_base$path'));
    _headers.forEach(rq.headers.set);
    rq.write(jsonEncode(body));
    final rs = await rq.close().timeout(const Duration(seconds: 20));
    final text = await rs.transform(utf8.decoder).join();
    if (rs.statusCode != 200) throw ApiException(rs.statusCode, text);
    return jsonDecode(text) as Map<String, dynamic>;
  }

  /// 免鉴权健康检查：数据版本号 + 最新客户端版本号（APP 自更新用）。
  Future<({int version, String? app})> health() async {
    final rq = await _http().getUrl(Uri.parse('$_base/healthz'));
    final rs = await rq.close().timeout(const Duration(seconds: 15));
    final body = await rs.transform(utf8.decoder).join();
    if (rs.statusCode != 200) throw ApiException(rs.statusCode, body);
    final d = jsonDecode(body) as Map<String, dynamic>;
    return (version: d['version'] as int, app: d['app'] as String?);
  }

  /// 下载最新 APP 更新包到 [savePath]（TLS 钉扎与业务请求一致）。
  Future<void> downloadApk(String savePath,
      {void Function(int received, int total)? onProgress}) async {
    final rq = await _http().getUrl(Uri.parse('$_base/api/app/latest'));
    final rs = await rq.close().timeout(const Duration(seconds: 300));
    if (rs.statusCode != 200) throw ApiException(rs.statusCode, '下载更新包失败');
    final total = rs.contentLength;
    final tmp = '$savePath.tmp';
    final sink = File(tmp).openWrite();
    var received = 0;
    try {
      await for (final chunk in rs) {
        received += chunk.length;
        sink.add(chunk);
        if (onProgress != null && total > 0) onProgress(received, total);
      }
      await sink.flush();
    } finally {
      await sink.close();
    }
    await File(tmp).rename(savePath);
  }

  /// since=0 即全量（含墓碑）。
  Future<({int version, List<Map<String, dynamic>> items})> changes({required int since}) async {
    final d = await _get('/api/changes?since=$since');
    return (
      version: d['version'] as int,
      items: (d['items'] as List).cast<Map<String, dynamic>>(),
    );
  }

  Future<({int accepted, int version})> pushItems(List<Map<String, dynamic>> items) async {
    final d = await _post('/api/changes', {'items': items});
    return (accepted: d['accepted'] as int, version: d['version'] as int);
  }

  void close() => _client?.close();
}

class ApiException implements Exception {
  ApiException(this.statusCode, this.body);
  final int statusCode;
  final String body;
  @override
  String toString() => 'HTTP $statusCode';
}
