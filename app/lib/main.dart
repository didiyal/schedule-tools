import 'dart:async';

import 'package:flutter/material.dart';
import 'package:path_provider/path_provider.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'api.dart';
import 'engine.dart';
import 'notify.dart';
import 'todo.dart';

void main() {
  runApp(const TodoTimerApp());
}

const _brand = Color(0xFF3370FF);
const _green = Color(0xFF2BA245);

class TodoTimerApp extends StatelessWidget {
  const TodoTimerApp({super.key});

  @override
  Widget build(BuildContext context) => MaterialApp(
        title: '待办计时器',
        theme: ThemeData(
          colorScheme: ColorScheme.fromSeed(seedColor: _brand),
          useMaterial3: true,
        ),
        home: const RootPage(),
      );
}

class RootPage extends StatefulWidget {
  const RootPage({super.key});
  @override
  State<RootPage> createState() => _RootPageState();
}

class _RootPageState extends State<RootPage> with WidgetsBindingObserver {
  SyncEngine? _engine;
  TodoApi? _api;
  Timer? _poll;
  final _notifier = RunNotifier();
  String _status = '';
  bool _todayOnly = false;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
    _boot();
  }

  Future<void> _boot() async {
    final prefs = await SharedPreferences.getInstance();
    final server = prefs.getString('server') ?? '';
    final token = prefs.getString('token') ?? '';
    final dir = await getApplicationDocumentsDirectory();
    final engine = SyncEngine(store: LocalStore('${dir.path}/todos.json'), now: _now);
    await engine.loadLocal();
    if (!mounted) return;
    setState(() {
      _engine = engine;
      _status = server.isEmpty ? '未配置服务器' : '本地 ${engine.todos.length} 条';
    });
    await _notifier.init(onPauseAction: _pauseFromNotification);
    _notifier.sync(engine.todos);
    if (server.isNotEmpty && token.isNotEmpty) await _connect(server, token, persist: false);
  }

  /// 通知栏「暂停」动作（前台路径）：停表固化并走 _write 上行。
  void _pauseFromNotification(String id) {
    final e = _engine;
    if (e == null) return;
    final t = e.todos.where((x) => x.id == id).firstOrNull;
    if (t == null || !t.running) return;
    t.elapsed = t.liveElapsed;
    t.running = false;
    t.lastStartedAt = null;
    _write(t);
  }

  static int _now() => DateTime.now().millisecondsSinceEpoch ~/ 1000;

  static void _log(String m) => debugPrint('[TodoTimer] $m');

  Future<void> _connect(String server, String token, {required bool persist}) async {
    final api = TodoApi(server: server, token: token, pinnedSha256: TodoApi.defaultPinnedSha256);
    try {
      _log('connecting $server');
      final v = await api.healthVersion();
      _log('healthz ok, server version $v');
      if (persist) {
        final prefs = await SharedPreferences.getInstance();
        await prefs.setString('server', server);
        await prefs.setString('token', token);
      }
      if (!mounted) return;
      setState(() {
        _api = api;
        _status = '已连接（服务器版本 $v）';
      });
      await _syncOnce();
      _poll?.cancel();
      _poll = Timer.periodic(const Duration(seconds: 30), (_) => _syncOnce());
    } catch (e) {
      _log('connect failed: $e');
      if (!mounted) return;
      setState(() => _status = '连接失败：$e');
      if (persist) {
        ScaffoldMessenger.of(context)
            .showSnackBar(SnackBar(content: Text('连接失败：$e')));
      }
    }
  }

  Future<void> _syncOnce() async {
    final api = _api, engine = _engine;
    if (api == null || engine == null) return;
    try {
      final r = await engine.round(
          (items) => api.pushItems(items),
          ({required int since}) => api.changes(since: since));
      _log('sync ok: pushed=${r.pushed} merged=${r.merged} version=${r.version}');
      _notifier.sync(engine.todos);   // 远端启停/改名后通知跟随
      if (!mounted) return;
      setState(() {
        _status = '已同步 ${TimeOfDay.now().format(context)}'
            '${engine.hasPending || r.pushed > 0 ? '（上行 ${r.pushed} 条）' : ''}';
      });
    } catch (e) {
      _log('sync failed: $e');
      if (!mounted) return;
      setState(() => _status = '同步失败：$e');
    }
  }

  Future<void> _write(Todo t) async {
    _engine!.touch(t);
    await _engine!.saveLocal();
    if (mounted) setState(() {});
    _notifier.sync(_engine!.todos);
    _syncOnce();
  }

  Future<void> _remove(Todo t) async {
    final ok = await showDialog<bool>(
      context: context,
      builder: (c) => AlertDialog(
        title: const Text('删除任务？'),
        content: Text('「${t.text}」将从所有设备删除。'),
        actions: [
          TextButton(onPressed: () => Navigator.pop(c, false), child: const Text('取消')),
          FilledButton(onPressed: () => Navigator.pop(c, true), child: const Text('删除')),
        ],
      ),
    );
    if (ok != true) return;
    _engine!.deleteLocal(t);
    await _engine!.saveLocal();
    if (mounted) setState(() {});
    _syncOnce();
  }

  Future<void> _add(String text, String? parentId) async {
    final e = _engine!;
    final maxId = e.todos.fold(0, (m, t) {
      final n = int.tryParse(t.id) ?? 0;
      return n > m ? n : m;
    });
    final parent =
        parentId == null ? null : e.todos.where((t) => t.id == parentId).firstOrNull;
    final t = Todo(
      id: '${maxId + 1}',
      text: text,
      parent: parentId,
      order: e.todos.map((x) => x.order).fold(0, (a, b) => a > b ? a : b) + 1,
      date: parent?.date ?? Todo.todayStr(),
    );
    e.todos.add(t);
    await _write(t);
  }

  void _toggleRun(Todo t) {
    if (t.running) {
      t.elapsed = t.liveElapsed;
      t.running = false;
      t.lastStartedAt = null;
    } else {
      t.running = true;
      t.lastStartedAt = _now();
    }
    _write(t);
  }

  void _toggleDone(Todo t) {
    t.completed = !t.completed;
    if (t.completed) {
      if (t.running) {
        t.elapsed = t.liveElapsed;
        t.running = false;
        t.lastStartedAt = null;
      }
      // 补卡语义（与桌面一致）：完成归入任务当前所属日期，不强制改期到今天
    }
    _write(t);
  }

  String _fmt(int s) {
    final h = s ~/ 3600, m = s % 3600 ~/ 60, sec = s % 60;
    return h > 0
        ? '$h:${m.toString().padLeft(2, '0')}:${sec.toString().padLeft(2, '0')}'
        : '${m.toString().padLeft(2, '0')}:${sec.toString().padLeft(2, '0')}';
  }

  // ── 树形视图：日期倒序日组，组内 DFS（完成节点折叠子树）──

  List<_Row> _rows() {
    final e = _engine!;
    final today = Todo.todayStr();
    final children = <String?, List<Todo>>{};
    for (final t in e.todos) {
      (children[t.parent] ??= []).add(t);
    }
    for (final l in children.values) {
      l.sort((a, b) {
        if (a.starred != b.starred) return a.starred ? -1 : 1;
        return a.order.compareTo(b.order);
      });
    }
    final dates = e.todos.map((t) => t.date).toSet().toList()
      ..sort((a, b) => b.compareTo(a));

    final out = <_Row>[];
    for (final d in dates) {
      if (_todayOnly && d != today) continue;
      final roots =
          (children[null] ?? const <Todo>[]).where((t) => t.date == d).toList();
      if (roots.isEmpty) continue;
      out.add(_Row.header(d == today ? '今日 · ${roots.length} 项' : d));
      void emit(Todo t, int depth) {
        out.add(_Row.todo(t, depth));
        if (t.completed) return;
        for (final c in children[t.id] ?? const <Todo>[]) {
          emit(c, depth + 1);
        }
      }

      for (final r in roots) {
        emit(r, 0);
      }
    }
    return out;
  }

  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    if (state == AppLifecycleState.resumed) _syncOnce();
  }

  @override
  void dispose() {
    _poll?.cancel();
    WidgetsBinding.instance.removeObserver(this);
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final e = _engine;
    if (e == null) {
      return const Scaffold(body: Center(child: CircularProgressIndicator()));
    }
    return Scaffold(
      appBar: AppBar(
        title: const Text('待办计时器'),
        actions: [
          IconButton(
              icon: const Icon(Icons.today),
              tooltip: '只看今日',
              onPressed: () => setState(() => _todayOnly = !_todayOnly),
              color: _todayOnly ? _brand : null),
          IconButton(icon: const Icon(Icons.sync), tooltip: '立即同步', onPressed: _syncOnce),
          IconButton(icon: const Icon(Icons.settings), tooltip: '设置', onPressed: _openSettings),
        ],
      ),
      body: Column(children: [
        Padding(
            padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 4),
            child: Align(
                alignment: Alignment.centerLeft,
                child:
                    Text(_status, style: TextStyle(fontSize: 11, color: Colors.grey[600])))),
        Expanded(child: _buildList(e)),
      ]),
      floatingActionButton: FloatingActionButton(
          onPressed: () => _addDialog(context, null), child: const Icon(Icons.add)),
    );
  }

  Widget _buildList(SyncEngine e) {
    final rows = _rows();
    if (rows.isEmpty) {
      return Center(
          child: Text(e.todos.isEmpty ? '暂无任务' : '（视图下无任务）',
              style: TextStyle(color: Colors.grey[500])));
    }
    return RefreshIndicator(
      onRefresh: _syncOnce,
      child: ListView.builder(
        itemCount: rows.length,
        itemBuilder: (context, i) {
          final r = rows[i];
          if (r.isHeader) {
            return Container(
                padding: const EdgeInsets.fromLTRB(16, 14, 16, 4),
                alignment: Alignment.centerLeft,
                child: Text(r.header!,
                    style: TextStyle(
                        fontSize: 12,
                        fontWeight: FontWeight.bold,
                        color: Colors.grey[600])));
          }
          final t = r.todo!;
          final canHaveChildren = _depthOf(t) < 2 && !t.completed;
          return ListTile(
            contentPadding: EdgeInsets.only(left: 16 + r.depth * 20.0, right: 8),
            leading: Checkbox(
                value: t.completed,
                onChanged: (_) => _toggleDone(t),
                shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(4))),
            title: Row(children: [
              if (t.rolledAt != null)
                Padding(
                    padding: const EdgeInsets.only(right: 5),
                    child: Text('已结转',
                        style: TextStyle(fontSize: 10, color: Colors.grey[400]))),
              Expanded(
                  child: Text(t.text,
                      style: TextStyle(
                          fontSize: 14,
                          decoration:
                              t.completed ? TextDecoration.lineThrough : null,
                          color: t.completed
                              ? Colors.grey
                              : (t.parent == null ? null : Colors.black87)))),
            ]),
            subtitle: t.liveElapsed > 0 || t.running
                ? Text(_fmt(t.liveElapsed),
                    style: TextStyle(fontSize: 11, color: t.running ? _green : Colors.grey))
                : null,
            trailing: SizedBox(
                width: 118,
                child: Row(mainAxisSize: MainAxisSize.min, children: [
                  if (t.starred) const Icon(Icons.star, size: 15, color: Color(0xFFF5A623)),
                  if (canHaveChildren)
                    IconButton(
                        icon: const Icon(Icons.add_circle_outline, size: 20),
                        tooltip: '加子任务',
                        onPressed: () => _addDialog(context, t.id)),
                  IconButton(
                      icon: Icon(
                          t.running
                              ? Icons.pause_circle_outline
                              : Icons.play_circle_outline,
                          size: 22,
                          color: t.running ? _green : Colors.grey),
                      tooltip: t.running ? '暂停' : '开始',
                      onPressed: () => _toggleRun(t)),
                  IconButton(
                      icon: const Icon(Icons.delete_outline, size: 20, color: Colors.grey),
                      tooltip: '删除',
                      onPressed: () => _remove(t)),
                ])),
          );
        },
      ),
    );
  }

  int _depthOf(Todo t) {
    var d = 0;
    var cur = t;
    while (cur.parent != null) {
      final p = _engine!.todos.where((x) => x.id == cur.parent).firstOrNull;
      if (p == null) break;
      cur = p;
      d++;
    }
    return d;
  }

  Future<void> _addDialog(BuildContext context, String? parentId) async {
    final c = TextEditingController();
    final text = await showDialog<String>(
      context: context,
      builder: (c2) => AlertDialog(
        title: Text(parentId == null ? '新建任务' : '新建子任务'),
        content: TextField(controller: c, autofocus: true,
            decoration: const InputDecoration(hintText: '任务内容')),
        actions: [
          TextButton(onPressed: () => Navigator.pop(c2), child: const Text('取消')),
          FilledButton(onPressed: () => Navigator.pop(c2, c.text.trim()),
              child: const Text('添加')),
        ],
      ),
    );
    if (text != null && text.isNotEmpty) await _add(text, parentId);
  }

  Future<void> _openSettings() async {
    final prefs = await SharedPreferences.getInstance();
    final serverC = TextEditingController(text: prefs.getString('server') ?? '');
    final tokenC = TextEditingController(text: prefs.getString('token') ?? '');
    final ok = await showDialog<bool>(
      context: context,
      builder: (c) => AlertDialog(
        title: const Text('同步设置'),
        content: Column(mainAxisSize: MainAxisSize.min, children: [
          TextField(controller: serverC,
              decoration: const InputDecoration(
                  labelText: '服务器地址', hintText: 'https://47.112.114.139')),
          const SizedBox(height: 12),
          TextField(controller: tokenC,
              decoration: const InputDecoration(
                  labelText: '设备令牌（桌面端生成配对码换取，或直接粘贴）')),
        ]),
        actions: [
          if (_api != null)
            TextButton(
              onPressed: () async {
                await prefs.remove('server');
                await prefs.remove('token');
                _api?.close();
                _api = null;
                _poll?.cancel();
                if (context.mounted) Navigator.pop(c);
                setState(() => _status = '未配置服务器');
              },
              child: const Text('断开')),
          TextButton(onPressed: () => Navigator.pop(c, false), child: const Text('取消')),
          FilledButton(onPressed: () => Navigator.pop(c, true), child: const Text('连接')),
        ],
      ),
    );
    if (ok == true && serverC.text.isNotEmpty && tokenC.text.isNotEmpty) {
      await _connect(serverC.text.trim(), tokenC.text.trim(), persist: true);
    }
  }
}

class _Row {
  _Row.todo(this.todo, this.depth) : isHeader = false, header = null;
  _Row.header(this.header) : isHeader = true, todo = null, depth = 0;
  final bool isHeader;
  final String? header;
  final Todo? todo;
  final int depth;
}
