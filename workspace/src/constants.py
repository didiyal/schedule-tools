# ── 飞书风格配色 ──────────────────────────────────────────
C = {
    "blue":          "#3370FF",
    "blue_hover":    "#4E83FD",
    "blue_light":    "#E1EAFF",
    "bg":            "#F5F6F7",
    "surface":       "#FFFFFF",
    "text":          "#1F2329",
    "text_sec":      "#646A73",
    "text_ter":      "#8F959E",
    "border":        "#E5E6EB",
    "green":         "#00B578",
    "green_hover":   "#00C788",
    "green_light":   "#E8F8EF",
    "orange":        "#FF8F1F",
    "orange_hover":  "#FFA340",
    "red":           "#F53F3F",
    "red_hover":     "#F75555",
    "red_light":     "#FFE4E4",
    "shadow":        "#DEE0E4",
    "strikethrough": "#BBBBC4",
}

FONT = "Microsoft YaHei"

# 应用版本号（安装器升级检测 + 顶栏显示）。改动后同步 installer/installer.iss 的 AppVersion。
APP_VERSION = "1.2.0"

# 分页：列表视图一次渲染的最大卡片条目数，超出尾部走「加载更多」
PAGE_SIZE = 30

# 圆角（customtkinter corner_radius 用）—— spec §5.2
RADIUS = {
    "card": 10,
    "button": 8,
    "pill": 999,
}

WEEKDAYS = ["周一", "周二", "周三", "周四", "周五", "周六", "周日"]
