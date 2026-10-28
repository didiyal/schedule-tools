package com.shiwujie.todo_timer

import android.content.Context
import android.os.Build
import android.os.VibrationEffect
import android.os.Vibrator
import android.os.VibratorManager
import io.flutter.embedding.android.FlutterActivity
import io.flutter.embedding.engine.FlutterEngine
import io.flutter.plugin.common.MethodChannel

class MainActivity : FlutterActivity() {
    // 半小时计时提醒震动（2026-10-29）：Dart 侧 MethodChannel('todo_timer/vibrate') 调用
    private val channelName = "todo_timer/vibrate"

    override fun configureFlutterEngine(flutterEngine: FlutterEngine) {
        super.configureFlutterEngine(flutterEngine)
        MethodChannel(flutterEngine.dartExecutor.binaryMessenger, channelName)
            .setMethodCallHandler { call, result ->
                when (call.method) {
                    "vibrate" -> {
                        val pattern = (call.argument<List<Int>>("pattern")
                            ?: listOf(0, 200, 150, 200)).map { it.toLong() }
                        vibrate(pattern)
                        result.success(null)
                    }
                    else -> result.notImplemented()
                }
            }
    }

    private fun vibrate(pattern: List<Long>) {
        val vibrator = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            val vm = getSystemService(Context.VIBRATOR_MANAGER_SERVICE) as VibratorManager
            vm.defaultVibrator
        } else {
            @Suppress("DEPRECATION")
            getSystemService(Context.VIBRATOR_SERVICE) as Vibrator
        }
        if (!vibrator.hasVibrator()) return
        // API 26+ 用波形（repeat=-1 只震一轮）；更老的设备退化为一次性震动
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            vibrator.vibrate(VibrationEffect.createWaveform(pattern.toLongArray(), -1))
        } else {
            @Suppress("DEPRECATION")
            vibrator.vibrate(pattern.lastOrNull() ?: 200L)
        }
    }
}
