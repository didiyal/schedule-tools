import 'package:flutter/material.dart';

/// 待办计时器 APP 设计令牌 —— 与桌面端 Themes/TodoTimer.xaml（飞书浅色体系）同源。
/// 依据：ui-ux-pro-max 检索（Minimalism & Swiss / Productivity Tool，2026-10-28）+
/// frontend-design「去 Material 默认感」定调：
///   · 检索推荐色板（teal+orange）与品牌飞书蓝冲突 → 弃用，只借鉴其中性层级；
///   · 覆盖 Material 默认紫：colorScheme 全量替换，AppBar 白底无影、控件全品牌化；
///   · 中文正文走系统思源黑体（Android 自带，不打包字体），计时数字用等宽数字防跳动。
abstract final class AppTheme {
  // ── 品牌色（与桌面同源）──
  static const brand = Color(0xFF3370FF); // 飞书蓝
  static const brandDeep = Color(0xFF2A5DBF); // 按压态
  static const brandBg = Color(0xFFE7EFFB); // 品牌浅底（一级卡/横幅）
  static const brandBorder = Color(0xFFCBD9EE); // 品牌浅框

  // ── 中性色 ──
  static const bg = Color(0xFFF3F5F9); // 页面底
  static const card = Color(0xFFFFFFFF); // 卡面
  static const textMain = Color(0xFF1F2329); // 正文
  static const textSub = Color(0xFF646A73); // 次级
  static const textWeak = Color(0xFF8F959E); // 弱化
  static const divider = Color(0xFFEEF1F6); // 分割/描边

  // ── 语义色（与桌面同源）──
  static const running = Color(0xFF2BA245); // 计时中/完成绿
  static const runningBg = Color(0xFFEAF7EC); // 计时中浅绿底
  static const doneText = Color(0xFF9AA3AF); // 完成态文字（桌面同源）
  static const star = Color(0xFFF5A623); // 重点星
  static const danger = Color(0xFFF54A45); // 删除红
  static const warn = Color(0xFFFF8800); // 警示橙（今日空槽同源）

  static const radius = 12.0; // 卡片圆角
  static const cardShadow = BoxShadow(
    color: Color(0x141F2329), // 8% 正文黑
    blurRadius: 6,
    offset: Offset(0, 1),
  );

  static ThemeData light() {
    final base = ThemeData(useMaterial3: true, colorSchemeSeed: brand);
    final scheme = base.colorScheme.copyWith(
      primary: brand,
      onPrimary: Colors.white,
      primaryContainer: brandBg,
      onPrimaryContainer: brandDeep,
      secondary: brand,
      onSecondary: Colors.white,
      surface: card,
      onSurface: textMain,
      surfaceContainerHighest: bg,
      error: danger,
      onError: Colors.white,
      outline: divider,
    );
    return base.copyWith(
      colorScheme: scheme,
      scaffoldBackgroundColor: bg,
      appBarTheme: const AppBarTheme(
        backgroundColor: card,
        surfaceTintColor: Colors.transparent,
        elevation: 0,
        scrolledUnderElevation: 0,
        centerTitle: false,
        titleTextStyle: TextStyle(
            color: textMain, fontSize: 17, fontWeight: FontWeight.w700),
        iconTheme: IconThemeData(color: textSub, size: 22),
      ),
      checkboxTheme: CheckboxThemeData(
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(5)),
        side: const BorderSide(color: textWeak, width: 1.4),
        fillColor: WidgetStateProperty.resolveWith((states) =>
            states.contains(WidgetState.selected) ? brand : Colors.transparent),
      ),
      dialogTheme: DialogThemeData(
        backgroundColor: card,
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(16)),
        titleTextStyle: const TextStyle(
            color: textMain, fontSize: 16, fontWeight: FontWeight.w700),
      ),
      inputDecorationTheme: InputDecorationTheme(
        filled: true,
        fillColor: bg,
        hintStyle: const TextStyle(color: textWeak, fontSize: 14),
        labelStyle: const TextStyle(color: textSub, fontSize: 14),
        contentPadding:
            const EdgeInsets.symmetric(horizontal: 12, vertical: 12),
        border: OutlineInputBorder(
            borderRadius: BorderRadius.circular(10),
            borderSide: BorderSide.none),
        focusedBorder: OutlineInputBorder(
            borderRadius: BorderRadius.circular(10),
            borderSide: const BorderSide(color: brand, width: 1.4)),
      ),
      floatingActionButtonTheme: FloatingActionButtonThemeData(
        backgroundColor: brand,
        foregroundColor: Colors.white,
        elevation: 0,
        highlightElevation: 0,
        focusElevation: 0,
        hoverElevation: 0,
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(16)),
      ),
      snackBarTheme: SnackBarThemeData(
        backgroundColor: textMain,
        contentTextStyle: const TextStyle(color: Colors.white, fontSize: 13),
        behavior: SnackBarBehavior.floating,
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(10)),
      ),
      progressIndicatorTheme: const ProgressIndicatorThemeData(color: brand),
      dividerTheme: const DividerThemeData(color: divider, thickness: 1),
      textButtonTheme: TextButtonThemeData(
        style: TextButton.styleFrom(
            foregroundColor: brand,
            textStyle: const TextStyle(fontWeight: FontWeight.w600)),
      ),
      filledButtonTheme: FilledButtonThemeData(
        style: FilledButton.styleFrom(
            backgroundColor: brand,
            foregroundColor: Colors.white,
            textStyle: const TextStyle(fontWeight: FontWeight.w600)),
      ),
    );
  }
}
