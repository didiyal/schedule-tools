import 'dart:convert';
import 'dart:io';

import 'package:crypto/crypto.dart';

/// 服务器 API 客户端（v1.5 S3）。
/// TLS：服务器为自签证书，[pinnedSha256] 非空时逐请求比对证书指纹（hex 冒号分隔），
/// 不匹配即抛 CertificateException——钉证书是安全边界，别用全局 allowBadCert 兜底。
class TodoApi {
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

  /// 免鉴权健康检查；顺带返回服务器版本号。
  Future<int> healthVersion() async {
    final rq = await _http().getUrl(Uri.parse('$_base/healthz'));
    final rs = await rq.close().timeout(const Duration(seconds: 15));
    final body = await rs.transform(utf8.decoder).join();
    if (rs.statusCode != 200) throw ApiException(rs.statusCode, body);
    return (jsonDecode(body) as Map<String, dynamic>)['version'] as int;
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
