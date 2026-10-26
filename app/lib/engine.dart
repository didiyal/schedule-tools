import 'dart:async';
import 'todo.dart';

/// 手机端同步引擎（v1.5 S3）：
///   拉：since=0 全量 → 增量轮询（30s），合并进本地权威列表；
///   推：本地编辑 → updatedAt=now 立即上行；失败进入待推队列由下轮心跳重试。
/// 单设备手机端语义：远端 LWW 大者胜（手机不主动制造并发写，本地写即时上行）。
class SyncEngine {
  SyncEngine({required this.store, required this.now});
  final LocalStore store;
  final int Function() now;

  List<Todo> todos = [];
  int serverVersion = 0;
  final List<Map<String, dynamic>> _pending = [];   // 上行失败的条目（含墓碑）
  bool get hasPending => _pending.isNotEmpty;

  Future<void> loadLocal() async {
    final d = await store.load();
    todos = d.todos;
    serverVersion = d.serverVersion;
  }

  Future<void> saveLocal() => store.save(todos, serverVersion);

  /// 应用一条远端变更；返回列表是否有变化。
  bool applyRemote(({String id, int updatedAt, int? deletedAt, Todo? todo}) r) {
    final i = todos.indexWhere((t) => t.id == r.id);
    if (r.deletedAt != null) {
      if (i < 0) return false;
      todos.removeAt(i);
      return true;
    }
    if (r.todo == null) return false;
    final remote = r.todo!;
    if (i < 0) {
      todos.add(remote);
      return true;
    }
    if (r.updatedAt > todos[i].updatedAt) {
      todos[i] = remote;
      return true;
    }
    return false;
  }

  /// 本地写入：置 updatedAt=now，排队上行。
  void touch(Todo t) {
    t.updatedAt = now();
    _pending.add(remoteItem(t));
  }

  /// 本地删除（手机端只允许叶子删除；父任务由桌面端管理）。
  void deleteLocal(Todo t) {
    todos.remove(t);
    _pending.add(remoteItem(t, deletedAt: now()));
  }

  /// 合并一批远端条目（拉取回调）。
  bool mergeRemote(List<Map<String, dynamic>> items) {
    var changed = false;
    for (final it in items) {
      final r = parseRemoteItem(it);
      // 本地待推条目优先（尚未上行的本地修改不被同 id 远端旧数据覆盖）
      final idx = todos.indexWhere((t) => t.id == r.id);
      if (idx >= 0 && todos[idx].updatedAt >= r.updatedAt && r.deletedAt == null) continue;
      if (applyRemote(r)) changed = true;
    }
    return changed;
  }

  /// 与服务器同步一轮：先冲待推队列，再拉增量。
  Future<SyncRoundResult> round(
      Future<({int accepted, int version})> Function(List<Map<String, dynamic>>) push,
      Future<({int version, List<Map<String, dynamic>> items})> Function({required int since}) pull) async {
    var pushed = 0;
    if (_pending.isNotEmpty) {
      final batch = List<Map<String, dynamic>>.from(_pending);
      final r = await push(batch);
      _pending.removeRange(0, batch.length);
      pushed = r.accepted;
      if (r.version > serverVersion) serverVersion = r.version;
    }
    final d = await pull(since: serverVersion);
    final changed = mergeRemote(d.items);
    serverVersion = d.version;
    await saveLocal();
    return SyncRoundResult(pushed: pushed, merged: changed, version: serverVersion);
  }
}

class SyncRoundResult {
  SyncRoundResult({required this.pushed, required this.merged, required this.version});
  final int pushed;
  final bool merged;
  final int version;
}
