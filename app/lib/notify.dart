import 'package:flutter/foundation.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_local_notifications/flutter_local_notifications.dart';
import 'package:path_provider/path_provider.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'api.dart';
import 'todo.dart';

final _plugin = FlutterLocalNotificationsPlugin();
const _notifId = 42;

/// 计时常驻通知（2026-10-28 用户需求）：有 running 任务时通知栏常驻显示
/// 「任务名 + 系统原生秒表」，点通知回 APP，「暂停」按钮停表并上行服务器。
/// 前台动作走 UI 引擎（onPauseAction 回调）；进程被杀后的动作在后台 isolate
/// 直接读写 todos.json，尽力直推服务器（失败不阻塞——下次打开 APP 会冲队列）。
class RunNotifier {
  void Function(String id)? _onPauseAction;
  String? _shownId;          // 同值守卫：同一任务同一轮（开始时刻/文本没变）不重发
  int? _shownStart;
  String? _shownText;

  Future<void> init({required void Function(String id) onPauseAction}) async {
    _onPauseAction = onPauseAction;
    const settings = InitializationSettings(
        android: AndroidInitializationSettings('@mipmap/ic_launcher'));
    await _plugin.initialize(
      settings,
      onDidReceiveNotificationResponse: _onResponse,
      onDidReceiveBackgroundNotificationResponse: pauseBackground,
    );
    try {
      final android = _plugin.resolvePlatformSpecificImplementation<
          AndroidFlutterLocalNotificationsPlugin>();
      await android?.requestNotificationsPermission();   // Android 13+，已授权则 no-op
    } catch (_) {}
  }

  void _onResponse(NotificationResponse r) {
    if (r.actionId == 'pause' && _shownId != null) _onPauseAction?.call(_shownId!);
  }

  /// 按当前任务表刷新通知（状态变更/每轮同步后调用；内部同值守卫防闪烁）。
  Future<void> sync(List<Todo> todos) async {
    if (defaultTargetPlatform != TargetPlatform.android) return;
    final running = todos.where((t) => t.running && !t.completed).toList();
    if (running.isEmpty) {
      if (_shownId != null) await _plugin.cancel(_notifId);
      _shownId = null;
      return;
    }
    final t = running.first;
    if (t.id == _shownId && t.lastStartedAt == _shownStart && t.text == _shownText) {
      return;
    }
    final nowMs = DateTime.now().millisecondsSinceEpoch;
    final details = NotificationDetails(
      android: AndroidNotificationDetails(
        'run', '计时中',
        channelDescription: '正在计时的任务',
        ongoing: true,
        autoCancel: false,
        onlyAlertOnce: true,
        importance: Importance.low,
        priority: Priority.low,
        showWhen: true,
        usesChronometer: true,          // 系统原生秒表：自动走字，无需轮询更新
        when: nowMs - t.elapsed * 1000, // 扣掉历史累计，秒表从真实进度起走
        actions: const [AndroidNotificationAction('pause', '暂停')],
      ),
    );
    await _plugin.show(_notifId, '⏱ ${t.text}', '计时中', details);
    _shownId = t.id;
    _shownStart = t.lastStartedAt;
    _shownText = t.text;
  }
}

/// 进程被杀时点「暂停」的后台入口（必须是顶层函数）。
@pragma('vm:entry-point')
Future<void> pauseBackground(NotificationResponse r) async {
  if (r.actionId != 'pause') return;
  WidgetsFlutterBinding.ensureInitialized();
  try {
    final prefs = await SharedPreferences.getInstance();
    final server = prefs.getString('server') ?? '';
    final token = prefs.getString('token') ?? '';
    final dir = await getApplicationDocumentsDirectory();
    final store = LocalStore('${dir.path}/todos.json');
    final d = await store.load();
    Todo? hit;
    for (final t in d.todos) {
      if (t.running && !t.completed) {
        hit = t;
        break;
      }
    }
    if (hit == null) return;
    final now = DateTime.now().millisecondsSinceEpoch ~/ 1000;
    if (hit.lastStartedAt != null) {
      hit.elapsed += (now - hit.lastStartedAt!).clamp(0, 24 * 3600);
    }
    hit.running = false;
    hit.lastStartedAt = null;
    hit.updatedAt = now;
    await store.save(d.todos, d.serverVersion);
    if (server.isNotEmpty && token.isNotEmpty) {
      final api = TodoApi(server: server, token: token,
          pinnedSha256: TodoApi.defaultPinnedSha256);
      try {
        await api.pushItems([remoteItem(hit)]);
        api.close();
      } catch (_) {}   // 直推失败不阻塞：本地已停表，下次打开 APP 由 LWW 语义兜底
    }
    await _plugin.cancel(_notifId);
  } catch (_) {}
}
