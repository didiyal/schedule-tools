package com.shiwujie.todo_timer

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import io.flutter.embedding.engine.FlutterEngineCache
import io.flutter.plugin.common.MethodChannel

/**
 * 通知「暂停」动作直连通道（2026-10-29）：
 * FLN 的 ActionBroadcastReceiver→startEngine→EventSink 路径在本机不稳定
 * （原生 Gson "Missing type parameter" 崩溃 + engine 桥接时序），绕开它——
 * APP 进程存活时直接经 MethodChannel 把 actionId 递给 Dart 的
 * RunNotifier.init(onPauseAction:) 回调；进程不在时退回 FLN 后台 isolate 路径
 * （pauseBackground 仍注册在 FLN init 里，双保险）。
 */
class PauseActionReceiver : BroadcastReceiver() {
    companion object {
        const val ACTION = "com.shiwujie.todo_timer.PAUSE_ACTION"
    }

    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != ACTION) return
        val actionId = intent.getStringExtra("action_id") ?: return
        val todoId = if (actionId.startsWith("pause:")) actionId.substring(6) else return
        if (todoId.isEmpty()) return

        val engine = FlutterEngineCache.getInstance().get("my_engine_id")
        if (engine != null) {
            // 固定通道名；Dart 端在 init 里 setMethodCallHandler 接收
            MethodChannel(engine.dartExecutor.binaryMessenger, "todo_timer/pause_action")
                .invokeMethod("pause", todoId)
        }
        // engine 不在缓存（进程刚被拉起还没挂 engine）→ 交给 FLN 后台 isolate 路径
    }
}
