import 'dart:convert';
import 'dart:io';

/// 单条待办 —— JSON 契约与桌面端 TodoItem 逐字段对齐（snake_case）。
class Todo {
  Todo({
    required this.id,
    required this.text,
    this.parent,
    this.elapsed = 0,
    this.completed = false,
    this.visible = true,
    this.order = 0,
    this.starred = false,
    this.running = false,
    this.lastStartedAt,
    this.targetSeconds,
    this.reminded = false,
    required this.date,
    this.archivedAt,
    this.updatedAt = 0,
    this.rolledAt,
    this.rolledFrom,
  });

  String id;
  String text;
  String? parent;      // null = 顶层；与桌面一致：3 层树
  int elapsed;         // 已累计秒数
  bool completed;
  bool visible;        // 是否加入计时面板（APP 不渲染面板，但保持契约）
  int order;
  bool starred;
  bool running;
  int? lastStartedAt;  // 本次计时的开始时刻（秒级 unix）
  int? targetSeconds;
  bool reminded;
  String date;         // yyyy-MM-dd
  String? archivedAt;
  int updatedAt;       // LWW 时间戳
  String? rolledAt;    // 跨日结转标记（与桌面 v1.5 契约对齐——上行时必须回传，否则会抹掉桌面写入）
  String? rolledFrom;  // 今日承接根 → 结转的旧根 id

  /// 含运行中的实时时长（对齐桌面 LiveElapsed）。
  int get liveElapsed {
    if (!running || lastStartedAt == null) return elapsed;
    final now = DateTime.now().millisecondsSinceEpoch ~/ 1000;
    return elapsed + (now - lastStartedAt!).clamp(0, 24 * 3600);
  }

  factory Todo.fromJson(Map<String, dynamic> j) => Todo(
        id: j['id'] as String,
        text: (j['text'] ?? '') as String,
        parent: j['parent'] as String?,
        elapsed: (j['elapsed'] ?? 0) as int,
        completed: (j['completed'] ?? false) as bool,
        visible: (j['visible'] ?? true) as bool,
        order: (j['order'] ?? 0) as int,
        starred: (j['starred'] ?? false) as bool,
        running: (j['running'] ?? false) as bool,
        lastStartedAt: j['last_started_at'] as int?,
        targetSeconds: j['target_seconds'] as int?,
        reminded: (j['reminded'] ?? false) as bool,
        date: (j['date'] ?? '') as String,
        archivedAt: j['archived_at'] as String?,
        updatedAt: (j['updated_at'] ?? 0) as int,
        rolledAt: j['rolled_at'] as String?,
        rolledFrom: j['rolled_from'] as String?,
      );

  Map<String, dynamic> toJson() => {
        'id': id,
        'text': text,
        'parent': parent,
        'elapsed': elapsed,
        'completed': completed,
        'x': 0,
        'y': 0,
        'visible': visible,
        'order': order,
        'starred': starred,
        'running': running,
        'last_started_at': lastStartedAt,
        'target_seconds': targetSeconds,
        'reminded': reminded,
        'date': date,
        'archived_at': archivedAt,
        'updated_at': updatedAt,
        'rolled_at': rolledAt,
        'rolled_from': rolledFrom,
      };

  Todo copy() => Todo.fromJson(jsonDecode(jsonEncode(toJson())) as Map<String, dynamic>);

  static String todayStr() {
    final n = DateTime.now();
    return '${n.year}-${n.month.toString().padLeft(2, '0')}-${n.day.toString().padLeft(2, '0')}';
  }
}

/// 上行变更条目（服务端 /api/changes 的 items 元素）。
Map<String, dynamic> remoteItem(Todo t, {int? deletedAt}) => {
      'id': t.id,
      'updatedAt': t.updatedAt,
      'deletedAt': deletedAt,
      'json': t.toJson(),
    };

/// 服务端下行条目解析。
({String id, int updatedAt, int? deletedAt, Todo? todo}) parseRemoteItem(Map<String, dynamic> it) {
  final id = it['id'] as String;
  final ua = (it['updatedAt'] ?? 0) as int;
  final del = it['deletedAt'] as int?;
  Todo? t;
  final j = it['json'];
  if (j is Map<String, dynamic>) {
    try {
      t = Todo.fromJson(j);
    } catch (_) {
      t = null;
    }
  }
  return (id: id, updatedAt: ua, deletedAt: del, todo: t);
}

/// 本地持久化：JSON 文件（应用文档目录由 main 注入路径）。
class LocalStore {
  LocalStore(this.path);
  final String path;

  Future<({List<Todo> todos, int serverVersion})> load() async {
    final f = File(path);
    if (!await f.exists()) return (todos: <Todo>[], serverVersion: 0);
    try {
      final d = jsonDecode(await f.readAsString()) as Map<String, dynamic>;
      return (
        todos: (d['todos'] as List).map((e) => Todo.fromJson(e as Map<String, dynamic>)).toList(),
        serverVersion: (d['server_version'] ?? 0) as int,
      );
    } catch (_) {
      return (todos: <Todo>[], serverVersion: 0);
    }
  }

  Future<void> save(List<Todo> todos, int serverVersion) async {
    final tmp = '$path.tmp';
    await File(tmp).writeAsString(jsonEncode({
      'server_version': serverVersion,
      'todos': todos.map((t) => t.toJson()).toList(),
    }));
    await File(tmp).rename(path);
  }
}
