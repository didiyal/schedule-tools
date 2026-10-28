import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_local_notifications/flutter_local_notifications.dart';
import 'package:path_provider/path_provider.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'api.dart';
import 'todo.dart';

final _plugin = FlutterLocalNotificationsPlugin();
const _baseId = 42; // 每个 running 任务占一个槽：_baseId + 序号

String _actionOf(String todoId) => 'pause:$todoId';

String? _parseAction(String? actionId) =>
    actionId != null && actionId.startsWith('pause:') && actionId.length > 6
        ? actionId.substring(6)
        : null;

/// 计时常驻通知（2026-10-28 用户需求）：每个 running 任务一条独立通知
/// （2026-10-28 用户报：多任务进行时只能看到第一个）——「任务名 + 系统原生秒表」，
/// 点通知回 APP，「暂停」按钮只停对应任务（actionId 编码 pause:<id>）。
/// 前台动作走 UI 引擎（onPauseAction 回调）；进程被杀后的动作在后台 isolate
/// 直接读写 todos.json，尽力直推服务器（失败不阻塞——下次打开 APP 由 LWW 兜底）。
class RunNotifier {
  void Function(String id)? _onPauseAction;
  final Map<String, int> _slots = {}; // todoId -> 通知 id（占用中的槽）
  final Map<String, (int?, String)> _shown = {}; // todoId -> (start, text) 同值守卫

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
      // 不等待授权结果——系统弹窗挂起会阻塞 init 的调用方（boot 里的连接流程）
      unawaited(android?.requestNotificationsPermission());
    } catch (_) {}
  }

  void _onResponse(NotificationResponse r) {
    final id = _parseAction(r.actionId);
    if (id != null) _onPauseAction?.call(id);
  }

  /// 按当前任务表刷新通知（状态变更/每轮同步后调用；内部同值守卫防闪烁）。
  Future<void> sync(List<Todo> todos) async {
    if (defaultTargetPlatform != TargetPlatform.android) return;
    final running = todos.where((t) => t.running && !t.completed).toList();

    // 收回不再 running 的槽位
    final live = running.map((t) => t.id).toSet();
    for (final id in _slots.keys.where((k) => !live.contains(k)).toList()) {
      await _plugin.cancel(_slots.remove(id)!);
      _shown.remove(id);
    }

    // 每个任务一条通知（空槽按序分配；同一任务未变不重发）
    var next = 0;
    for (final t in running) {
      int notifId;
      if (_slots.containsKey(t.id)) {
        notifId = _slots[t.id]!;
      } else {
        while (_slots.values.contains(_baseId + next)) {
          next++;
        }
        notifId = _baseId + next;
        _slots[t.id] = notifId;
      }
      final shown = _shown[t.id];
      if (shown != null && shown == (t.lastStartedAt, t.text)) continue;
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
          actions: [AndroidNotificationAction(_actionOf(t.id), '暂停')],
        ),
      );
      await _plugin.show(notifId, '⏱ ${t.text}', '计时中', details);
      _shown[t.id] = (t.lastStartedAt, t.text);
    }
  }
}

/// 进程被杀时点「暂停」的后台入口（必须是顶层函数）。
@pragma('vm:entry-point')
Future<void> pauseBackground(NotificationResponse r) async {
  final targetId = _parseAction(r.actionId);
  if (targetId == null) return;
  WidgetsFlutterBinding.ensureInitialized();
  try {
    final prefs = await SharedPreferences.getInstance();
    final server = prefs.getString('server') ?? '';
    final token = prefs.getString('token') ?? '';
    final dir = await getApplicationDocumentsDirectory();
    final store = LocalStore('${dir.path}/todos.json');
    final d = await store.load();
    Todo? hit = d.todos
        .where((t) => t.id == targetId && t.running && !t.completed)
        .firstOrNull;
    hit ??= d.todos.where((t) => t.running && !t.completed).firstOrNull;
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
    await _plugin.cancel(_baseId); // 兜底清理旧版单通知残留
  } catch (_) {}
}
