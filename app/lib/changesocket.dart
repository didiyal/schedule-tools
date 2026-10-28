import 'dart:async';
import 'dart:io';

import 'api.dart';

/// 变更门铃 WebSocket（v1.4.7）：服务端在同空间有变更时秒级广播
/// `{"type":"change","version":N}`，客户端收到即触发一轮增量拉取——
/// 数据本体仍走 /api/changes（合并逻辑零重写），WS 只解决"实时性"。
/// 断线 10s 自动重连；每 25s 发 ping 保活（nginx read_timeout 60s）。
class ChangeSocket {
  ChangeSocket(
      {required this.server, required this.token, required this.onChanged});

  final String server;
  final String token;
  final void Function() onChanged;

  WebSocket? _ws;
  Timer? _retry;
  Timer? _ping;
  bool _stopped = false;

  Future<void> connect() async {
    if (_stopped) return;
    try {
      final uri = server.replaceFirst('https://', 'wss://') + '/api/ws';
      final hc = HttpClient()
        ..badCertificateCallback = (cert, host, port) =>
            TodoApi.verifyCert(cert, TodoApi.defaultPinnedSha256);
      final ws = await WebSocket.connect(uri,
          headers: {'Authorization': 'Bearer $token'}, customClient: hc);
      _retry?.cancel();
      _ws = ws;
      ws.listen((data) {
        if (data is String && data.contains('"change"')) onChanged();
      }, onDone: _retryLater, onError: (_) => _retryLater);
      _ping = Timer.periodic(const Duration(seconds: 25), (t) {
        if (_ws != ws || ws.readyState != WebSocket.open) {
          t.cancel();
          return;
        }
        ws.add('ping');
      });
    } catch (_) {
      _retryLater();
    }
  }

  void _retryLater() {
    _retry?.cancel();
    _retry = Timer(const Duration(seconds: 10), connect);
  }

  void dispose() {
    _stopped = true;
    _retry?.cancel();
    _ping?.cancel();
    _ws?.close();
  }
}
