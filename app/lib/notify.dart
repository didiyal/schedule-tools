import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_local_notifications/flutter_local_notifications.dart';
import 'package:path_provider/path_provider.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'api.dart';
import 'todo.dart';

final _plugin = FlutterLocalNotificationsPlugin();
const _baseId = 42; // 每个 running 任务占一个槽：_baseId + 序号

String _actionOf(String todoId) => 'pause:$todoId';

/// 通知 id 按任务 id 确定性派生：前台/后台 isolate/App 重启后算出的都是同一个 id，
/// 暂停后才能准确清掉对应通知（2026-10-28 用户报「通知栏暂停无效」——暂停成功但
/// 通知清不掉继续走字，看起来就是没停）。
int _notifIdFor(String todoId) {
  final n = int.tryParse(todoId);
  return _baseId + (n ?? (todoId.hashCode & 0xFFFF));
}

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

    // FLN 的 action 事件走静态 ActionEventSink——Android 端只在后台 isolate 的
    // callbackDispatcher 里订阅，主 isolate 从不 listen ⇒ 按钮事件全部积压丢失。
    // 主 isolate 主动 listen：onListen 时 sink 会回放缓存事件，后续实时直通。
    const actionsChannel =
        EventChannel('dexterous.com/flutter/local_notifications/actions');
    actionsChannel.receiveBroadcastStream().listen((event) {
      if (event is Map) {
        final id = _parseAction(event['actionId'] as String?);
        if (id != null) _onPauseAction?.call(id);
      }
    }, onError: (_) {});

    // 自建 PauseActionReceiver 直连通道（绕开 FLN 的引擎桥接）
    const pauseChannel = MethodChannel('todo_timer/pause_action');
    pauseChannel.setMethodCallHandler((call) async {
      if (call.method == 'pause' && call.arguments is String) {
        _onPauseAction?.call(call.arguments as String);
      }
      return null;
    });
  }

  void _onResponse(NotificationResponse r) {
    final id = _parseAction(r.actionId);
    if (id != null) _onPauseAction?.call(id);
  }

  /// 按当前任务表刷新通知（状态变更/每轮同步后调用；内部同值守卫防闪烁）。
  Future<void> sync(List<Todo> todos) async {
    if (defaultTargetPlatform != TargetPlatform.android) return;
    final running = todos.where((t) => t.running && !t.completed).toList();
    final liveIds = running.map((t) => t.id).toSet();

    // 收回不再计时/已删除任务的通知（确定性 id，重启后也有效）；
    // cancel 失败不阻塞后续 show（2026-10-28 实测：_plugin.cancel 偶发
    // PlatformException 把 sync 挂掉，导致其它任务的通知也不刷新）
    for (final id in _shown.keys.toSet()) {
      if (!liveIds.contains(id)) {
        try { await _plugin.cancel(_notifIdFor(id)); } catch (_) {}
        _shown.remove(id);
      }
    }

    // 每个任务一条通知；同一任务（开始时刻+文本）未变不重发
    for (final t in running) {
      final notifId = _notifIdFor(t.id);
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
      try {
        await _plugin.show(notifId, '⏱ ${t.text}', '计时中', details);
        _shown[t.id] = (t.lastStartedAt, t.text);
      } catch (e) {
        debugPrint('[TodoTimer] notif show failed: $e');
      }
    }
  }

  /// App 启动时清残留：重启后 _shown 为空，但通知 id 确定性可算，
  /// 非 running 任务的旧通知逐个清掉。
  /// cancel 偶发 PlatformException——必须逐条兜住，否则会炸断调用方
  /// 的 boot 流程（自动连接根本不执行，用户看到"要重新配对"）。
  Future<void> clearStale(List<Todo> todos) async {
    for (final t in todos) {
      if (t.running && !t.completed) continue;
      try { await _plugin.cancel(_notifIdFor(t.id)); } catch (_) {}
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
    await _plugin.cancel(_notifIdFor(hit.id)); // 清掉该任务的计时通知（通知消失=用户看到的“已暂停”）
  } catch (_) {}
}
