import 'dart:async';
import 'dart:ui' show FontFeature;

import 'package:flutter/material.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter/services.dart';
import 'package:package_info_plus/package_info_plus.dart';
import 'package:path_provider/path_provider.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'api.dart';
import 'changesocket.dart';
import 'engine.dart';
import 'notify.dart';
import 'theme.dart';
import 'todo.dart';
import 'updater.dart';

void main() {
  runApp(const TodoTimerApp());
}

const _brand = AppTheme.brand;

class TodoTimerApp extends StatelessWidget {
  const TodoTimerApp({super.key});

  @override
  Widget build(BuildContext context) => MaterialApp(
        title: '待办计时器',
        theme: AppTheme.light(),
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
  ChangeSocket? _changeSocket;
  bool _syncing = false;   // WS 门铃+30s 心跳+手动刷新并发去重
  String _localVersion = '';
  String? _docsPath;
  String? _updateVer;   // 服务器有新版本时的版本号（null=已是最新/未连接）
  final Set<String> _collapsed = {};   // 收起的主任务 id（本地记忆，不进同步契约）
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
    _docsPath = dir.path;
    final info = await PackageInfo.fromPlatform();
    _localVersion = info.version;
    final engine = SyncEngine(store: LocalStore('${dir.path}/todos.json'), now: _now);
    await engine.loadLocal();
    _collapsed.addAll(prefs.getStringList('collapsed') ?? const <String>[]);
    if (!mounted) return;
    setState(() {
      _engine = engine;
      _status = server.isEmpty ? '未配置服务器' : '本地 ${engine.todos.length} 条';
    });
    await _notifier.init(onPauseAction: _pauseFromNotification);
    await _notifier.clearStale(engine.todos);
    _notifier.sync(engine.todos);
    if (server.isNotEmpty && token.isNotEmpty) {
      _log('boot: saved server=$server token=${token.substring(0, token.length > 8 ? 8 : token.length)}… → auto-reconnect');
      _autoReconnect(server, token, attempts: 0);
    } else {
      _log('boot: no saved config (server="$server" token len=${token.length})');
    }
    _startVibrateTick();
  }

  /// 启动自动重连：移动网络冷启动/自签握手偶发超时，失败后 10s 重试（最多 5 次）。
  /// 配置本身在 SharedPreferences 里没丢——不能把"暂时连不上"显示成"要重新设置"。
  Future<void> _autoReconnect(String server, String token, {required int attempts}) async {
    final ok = await _connect(server, token, persist: false);
    if (ok || attempts >= 4 || !mounted) return;
    setState(() => _status = '自动重连中…（${attempts + 1}/5）');
    Future.delayed(const Duration(seconds: 10),
        () => _autoReconnect(server, token, attempts: attempts + 1));
  }

  Timer? _vibrateTick;   // 半小时提醒

  /// 计时中每半小时震动提醒（2026-10-29 用户需求）：只要还有任务在跑，
  /// 每 30 分钟短震两下。启动时把首个触发点对齐到「当前 running 任务的
  /// lastStartedAt + 30min 的整数倍」，之后每 30min 一轮。
  void _startVibrateTick() {
    _vibrateTick?.cancel();
    _vibrateTick = Timer.periodic(const Duration(minutes: 30), (_) {
      final e = _engine;
      if (e == null || !e.todos.any((t) => t.running && !t.completed)) return;
      _vibrate();
      _log('vibrate: 30min reminder');
    });
  }

  Future<void> _vibrate() async {
    try {
      if (defaultTargetPlatform != TargetPlatform.android) return;
      const channel = MethodChannel('todo_timer/vibrate');
      await channel.invokeMethod('vibrate', {'pattern': [0, 200, 150, 200]});
    } catch (e) {
      _log('vibrate failed: $e');
    }
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

  void _toggleCollapse(String id) async {
    if (!_collapsed.remove(id)) _collapsed.add(id);
    final prefs = await SharedPreferences.getInstance();
    await prefs.setStringList('collapsed', _collapsed.toList());
    if (mounted) setState(() {});
  }

  bool _hasChildren(String id) => _engine!.todos.any((x) => x.parent == id);

  /// 含子任务时长=自身+子树递归（对齐桌面 EffectiveElapsed；主任务不可单独计时）。
  int _effElapsed(Todo t, [Set<String>? seen]) {
    seen ??= <String>{};
    if (!seen.add(t.id)) return t.liveElapsed;
    var total = t.liveElapsed;
    for (final c in _engine!.todos.where((x) => x.parent == t.id)) {
      total += _effElapsed(c, seen);
    }
    return total;
  }

  static void _log(String m) => debugPrint('[TodoTimer] $m');

  Future<bool> _connect(String server, String token, {required bool persist}) async {
    final api = TodoApi(server: server, token: token, pinnedSha256: TodoApi.defaultPinnedSha256);
    try {
      _log('connecting $server');
      final h = await api.health();
      _log('healthz ok, server version ${h.version} app=${h.app}');
      if (persist) {
        final prefs = await SharedPreferences.getInstance();
        await prefs.setString('server', server);
        await prefs.setString('token', token);
      }
      if (!mounted) return true;
      setState(() {
        _api = api;
        _status = '已连接（服务器版本 ${h.version}）';
        _updateVer = Updater.newerVersion(h.app, _localVersion);
      });
      await _syncOnce();
      _poll?.cancel();
      _poll = Timer.periodic(const Duration(seconds: 30), (_) => _syncOnce());
      // 变更门铃：服务端同空间有变更时秒级广播，收到即拉（30s 心跳降级为兜底）
      _changeSocket?.dispose();
      _changeSocket = ChangeSocket(
          server: server, token: token, onChanged: () => _syncOnce());
      _changeSocket!.connect();
      return true;
    } catch (e) {
      _log('connect failed: $e');
      if (!mounted) return false;
      setState(() => _status = '连接失败：$e（配置已保存，稍后自动重试）');
      if (persist) {
        ScaffoldMessenger.of(context)
            .showSnackBar(SnackBar(content: Text('连接失败：$e')));
      }
      return false;
    }
  }

  Future<void> _syncOnce({bool tellUser = false}) async {
    _log('syncOnce tapped api=${_api != null} engine=${_engine != null} _syncing=$_syncing');
    final api = _api, engine = _engine;
    if (api == null || engine == null) {
      if (tellUser && mounted)
        ScaffoldMessenger.of(context)
            .showSnackBar(const SnackBar(content: Text('未配置服务器，先到设置里连接')));
      return;
    }
    if (_syncing) return;   // WS 门铃与心跳并发去重（合并/落盘非重入安全）
    _syncing = true;
    try {
      final r = await engine.round(
          (items) => api.pushItems(items),
          ({required int since}) => api.changes(since: since));
      _log('sync ok: pushed=${r.pushed} merged=${r.merged} version=${r.version}');
      _notifier.sync(engine.todos);   // 远端启停/改名后通知跟随
      if (!mounted) return;
      setState(() {
        _status = r.pushFailed
            ? '推送失败，本地改动待重试'
            : '已同步 ${TimeOfDay.now().format(context)}'
                '${engine.hasPending || r.pushed > 0 ? '（上行 ${r.pushed} 条）' : ''}';
      });
      if (tellUser) {
        ScaffoldMessenger.of(context).showSnackBar(SnackBar(
            content: Text(r.pushFailed
                ? '推送失败，本地改动待重试'
                : '已同步 ${engine.todos.length} 条任务')));
      }
    } catch (e) {
      _log('sync failed: $e');
      if (!mounted) return;
      setState(() => _status = '同步失败：$e');
      if (tellUser)
        ScaffoldMessenger.of(context)
            .showSnackBar(SnackBar(content: Text('同步失败：$e')));
    } finally {
      _syncing = false;
    }
  }

  Future<void> _write(Todo t) async {
    _engine!.touch(t);
    await _engine!.saveLocal();
    if (mounted) setState(() {});
    _notifier.sync(_engine!.todos);
    _syncOnce();
  }

  /// 更新横幅点击：下载更新包（进度对话框）→ 拉起系统安装器。
  Future<void> _doUpdate() async {
    final api = _api, ver = _updateVer, docs = _docsPath;
    if (api == null || ver == null || docs == null) return;
    final pct = ValueNotifier<int>(0);
    unawaited(showDialog(
        context: context,
        barrierDismissible: false,
        builder: (c) => PopScope(
              canPop: false,
              child: AlertDialog(
                title: const Text('下载更新'),
                content: ValueListenableBuilder<int>(
                  valueListenable: pct,
                  builder: (c, v, _) => Column(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      Text(v == 0 ? '连接服务器…' : '已下载 $v%'),
                      const SizedBox(height: 12),
                      LinearProgressIndicator(value: v == 0 ? null : v / 100),
                    ],
                  ),
                ),
              ),
            )));
    try {
      await Updater.downloadAndInstall(api, ver, docs,
          onProgress: (r, t) => pct.value = r * 100 ~/ t);
      if (mounted) {
        Navigator.of(context, rootNavigator: true).pop();
        setState(() => _updateVer = null);   // 安装器已拉起，横幅先撤
      }
    } catch (e) {
      if (mounted) {
        Navigator.of(context, rootNavigator: true).pop();
        ScaffoldMessenger.of(context)
            .showSnackBar(SnackBar(content: Text('更新失败：$e')));
      }
    }
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
      out.add(_Row.header(d == today ? '今日 · ${roots.length} 项' : d, isToday: d == today));
      void emit(Todo t, int depth) {
        out.add(_Row.todo(t, depth));
        if (t.completed) return;
        if (_collapsed.contains(t.id)) return;   // 已收起：子树不展开
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
          IconButton(
              icon: const Icon(Icons.sync),
              tooltip: '立即同步',
              onPressed: () => _syncOnce(tellUser: true)),
          IconButton(icon: const Icon(Icons.settings), tooltip: '设置', onPressed: _openSettings),
        ],
      ),
      body: Column(children: [
        Padding(
            padding: const EdgeInsets.fromLTRB(16, 6, 16, 4),
            child: Align(
                alignment: Alignment.centerLeft,
                child: Text(_status,
                    style: const TextStyle(
                        fontSize: 11, color: AppTheme.textWeak,
                        fontFeatures: [FontFeature.tabularFigures()])))),
        if (_updateVer != null)
          Padding(
            padding: const EdgeInsets.fromLTRB(12, 0, 12, 4),
            child: Material(
              color: AppTheme.brandBg,
              borderRadius: BorderRadius.circular(10),
              child: InkWell(
                onTap: _doUpdate,
                borderRadius: BorderRadius.circular(10),
                child: const Padding(
                  padding: EdgeInsets.symmetric(horizontal: 12, vertical: 10),
                  child: Row(children: [
                    Icon(Icons.system_update, size: 18, color: AppTheme.brand),
                    SizedBox(width: 8),
                    Expanded(
                        child: Text('发现新版本，点此更新',
                            style: TextStyle(
                                color: AppTheme.brandDeep,
                                fontSize: 13,
                                fontWeight: FontWeight.w600))),
                    Icon(Icons.chevron_right, size: 18, color: AppTheme.brand),
                  ]),
                ),
              ),
            ),
          ),
        Expanded(child: _buildList(e)),
      ]),
      floatingActionButton: FloatingActionButton.extended(
          onPressed: () => _addDialog(context, null),
          icon: const Icon(Icons.add, size: 20),
          label: const Text('新建',
              style: TextStyle(fontWeight: FontWeight.w600, fontSize: 14))),
    );
  }

  Widget _buildList(SyncEngine e) {
    final rows = _rows();
    if (rows.isEmpty) {
      return Center(
        child: Column(mainAxisSize: MainAxisSize.min, children: [
          Container(
            width: 64,
            height: 64,
            decoration:
                const BoxDecoration(color: AppTheme.brandBg, shape: BoxShape.circle),
            child: Icon(
                e.todos.isEmpty
                    ? Icons.check_circle_outline
                    : Icons.filter_alt_off,
                size: 30,
                color: AppTheme.brand),
          ),
          const SizedBox(height: 12),
          Text(e.todos.isEmpty ? '还没有任务，点右下角「新建」开始' : '当前视图下没有任务',
              style: const TextStyle(color: AppTheme.textWeak, fontSize: 13)),
        ]),
      );
    }
    return RefreshIndicator(
      color: AppTheme.brand,
      onRefresh: _syncOnce,
      child: ListView.builder(
        padding: const EdgeInsets.fromLTRB(12, 2, 12, 96),
        itemCount: rows.length,
        itemBuilder: (context, i) {
          final r = rows[i];
          if (r.isHeader) {
            return Padding(
              padding: const EdgeInsets.fromLTRB(6, 16, 6, 6),
              child: r.isToday
                  ? Container(
                      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 3),
                      decoration: BoxDecoration(
                          color: AppTheme.brand,
                          borderRadius: BorderRadius.circular(999)),
                      child: Text(r.header!,
                          style: const TextStyle(
                              fontSize: 12,
                              fontWeight: FontWeight.w700,
                              color: Colors.white)))
                  : Text(r.header!,
                      style: const TextStyle(
                          fontSize: 12,
                          fontWeight: FontWeight.w600,
                          color: AppTheme.textWeak)),
            );
          }
          return _taskCard(r);
        },
      ),
    );
  }

  /// 任务卡片 v3：根任务白卡+品牌细框+左色条，子任务缩进素卡；
  /// 有子任务=折叠箭头+无计时按钮（时长=子树之和，对齐桌面规则）；
  /// 操作收进右侧「⋯」底部面板（手机屏不适合多按钮，2026-10-28 用户拍板）。
  Widget _taskCard(_Row r) {
    final t = r.todo!;
    final isRoot = t.parent == null;
    final done = t.completed;
    final kids = _hasChildren(t.id);
    final collapsed = _collapsed.contains(t.id);
    return Container(
      margin: EdgeInsets.only(left: r.depth * 18.0, top: 3, bottom: 3),
      decoration: BoxDecoration(
        color: done ? AppTheme.bg : AppTheme.card,
        borderRadius: BorderRadius.circular(AppTheme.radius),
        border: isRoot && !done
            ? Border.all(color: AppTheme.brandBorder)
            : Border.all(color: AppTheme.divider),
        boxShadow: const [AppTheme.cardShadow],
      ),
      child: Row(children: [
        if (isRoot && !done)
          Container(
              width: 3,
              height: 34,
              margin: const EdgeInsets.only(left: 6),
              decoration: BoxDecoration(
                  color: AppTheme.brand,
                  borderRadius: BorderRadius.circular(2))),
        const SizedBox(width: 2),
        if (kids)
          IconButton(
              visualDensity: VisualDensity.compact,
              icon: Icon(collapsed ? Icons.chevron_right : Icons.expand_more,
                  size: 20, color: AppTheme.textWeak),
              tooltip: collapsed ? '展开子任务' : '收起子任务',
              onPressed: () => _toggleCollapse(t.id))
        else
          const SizedBox(width: 12),
        SizedBox(
          width: 22,
          child: Checkbox(
              value: t.completed,
              onChanged: (_) => _toggleDone(t),
              materialTapTargetSize: MaterialTapTargetSize.shrinkWrap),
        ),
        const SizedBox(width: 2),
        Expanded(
          child: Padding(
            padding: const EdgeInsets.symmetric(vertical: 9),
            child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
              Row(children: [
                if (t.starred)
                  const Padding(
                      padding: EdgeInsets.only(right: 4),
                      child: Icon(Icons.star, size: 14, color: AppTheme.star)),
                if (t.rolledAt != null)
                  Container(
                      margin: const EdgeInsets.only(right: 5),
                      padding:
                          const EdgeInsets.symmetric(horizontal: 5, vertical: 1),
                      decoration: BoxDecoration(
                          color: AppTheme.bg,
                          borderRadius: BorderRadius.circular(4)),
                      child: const Text('已结转',
                          style: TextStyle(
                              fontSize: 10, color: AppTheme.textWeak))),
                Expanded(
                    child: Text(t.text,
                        maxLines: 3,
                        overflow: TextOverflow.ellipsis,
                        style: TextStyle(
                            fontSize: 14,
                            fontWeight:
                                isRoot ? FontWeight.w600 : FontWeight.w400,
                            height: 1.3,
                            decoration:
                                done ? TextDecoration.lineThrough : null,
                            color: done
                                ? AppTheme.doneText
                                : (isRoot
                                    ? AppTheme.textMain
                                    : AppTheme.textSub)))),
              ]),
              if (_effElapsed(t) > 0 || t.running) ...[
                const SizedBox(height: 4),
                if (t.running)
                  Container(
                      padding: const EdgeInsets.symmetric(
                          horizontal: 7, vertical: 2),
                      decoration: BoxDecoration(
                          color: AppTheme.runningBg,
                          borderRadius: BorderRadius.circular(999)),
                      child: Row(mainAxisSize: MainAxisSize.min, children: [
                        const Icon(Icons.play_arrow,
                            size: 12, color: AppTheme.running),
                        const SizedBox(width: 2),
                        Text(_fmt(_effElapsed(t)),
                            style: const TextStyle(
                                fontSize: 11,
                                fontWeight: FontWeight.w600,
                                color: AppTheme.running,
                                fontFeatures: [FontFeature.tabularFigures()])),
                      ]))
                else
                  Text(_fmt(_effElapsed(t)),
                      style: TextStyle(
                          fontSize: 11,
                          color: AppTheme.textWeak,
                          fontFeatures: [FontFeature.tabularFigures()])),
                if (kids && !done)
                  Padding(
                      padding: const EdgeInsets.only(left: 4),
                      child: Text(collapsed ? '${_childCount(t)} 个子任务' : '共 ${_childCount(t)} 个子任务',
                          style: const TextStyle(
                              fontSize: 10, color: AppTheme.textWeak))),
              ],
            ]),
          ),
        ),
        if (!done)
          IconButton(
              visualDensity: VisualDensity.compact,
              icon: const Icon(Icons.more_horiz,
                  size: 22, color: AppTheme.textSub),
              tooltip: '操作',
              onPressed: () => _showActions(t)),
        const SizedBox(width: 2),
      ]),
    );
  }

  int _childCount(Todo t) =>
      _engine!.todos.where((x) => x.parent == t.id).length;

  /// 「⋯」操作面板：计时（仅无子任务）/加子任务/删除——替代原右侧三按钮。
  void _showActions(Todo t) {
    final kids = _hasChildren(t.id);
    final canAdd = _depthOf(t) < 2;
    showModalBottomSheet(
      context: context,
      backgroundColor: AppTheme.card,
      shape: const RoundedRectangleBorder(
          borderRadius: BorderRadius.vertical(top: Radius.circular(16))),
      builder: (c) => SafeArea(
        child: Column(mainAxisSize: MainAxisSize.min, children: [
          Padding(
              padding: const EdgeInsets.fromLTRB(20, 16, 20, 4),
              child: Align(
                  alignment: Alignment.centerLeft,
                  child: Text(t.text,
                      maxLines: 2,
                      overflow: TextOverflow.ellipsis,
                      style: const TextStyle(
                          fontSize: 15,
                          fontWeight: FontWeight.w600,
                          color: AppTheme.textMain)))),
          if (!kids && !t.completed)
            ListTile(
                leading: Icon(t.running ? Icons.pause_circle : Icons.play_circle,
                    color: t.running ? AppTheme.running : AppTheme.brand),
                title: Text(t.running ? '暂停计时' : '开始计时',
                    style: const TextStyle(fontSize: 14)),
                onTap: () {
                  Navigator.pop(c);
                  _toggleRun(t);
                }),
          if (!t.completed && canAdd)
            ListTile(
                leading: const Icon(Icons.subdirectory_arrow_right,
                    color: AppTheme.textSub),
                title: const Text('添加子任务',
                    style: TextStyle(fontSize: 14)),
                onTap: () {
                  Navigator.pop(c);
                  _addDialog(context, t.id);
                }),
          ListTile(
              leading: const Icon(Icons.delete_outline, color: AppTheme.danger),
              title: const Text('删除',
                  style: TextStyle(fontSize: 14, color: AppTheme.danger)),
              onTap: () {
                Navigator.pop(c);
                _remove(t);
              }),
          const SizedBox(height: 6),
        ]),
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
  _Row.todo(this.todo, this.depth) : isHeader = false, header = null, isToday = false;
  _Row.header(this.header, {this.isToday = false}) : isHeader = true, todo = null, depth = 0;
  final bool isHeader;
  final String? header;
  final bool isToday;
  final Todo? todo;
  final int depth;
}
