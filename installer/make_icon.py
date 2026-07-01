"""生成 app.ico —— 蓝底白色钟表图标（多尺寸），呼应「计时器」。

用法:  python installer/make_icon.py   →  installer/app.ico
依赖 pillow（pystray 已自带，无需另装）。
图标设计固定（非随机），保证每次构建/升级视觉一致。
"""
import math
import os
from PIL import Image, ImageDraw

_HERE = os.path.dirname(os.path.abspath(__file__))

BLUE = (51, 112, 255, 255)    # #3370FF 主色
WHITE = (255, 255, 255, 255)
SIZES = [16, 32, 48, 64, 128, 256]


def draw(size):
    s = size
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    pad = s * 0.085
    radius = s * 0.22
    # 蓝色圆角方底（四角透明，任务栏/资源管理器里更干净）
    d.rounded_rectangle([pad, pad, s - pad, s - pad], radius=radius, fill=BLUE)

    cx = cy = s / 2
    r = s * 0.30                # 钟面半径
    lw = max(2.0, s * 0.0625)   # 笔画宽度随尺寸缩放
    lwi = int(round(lw))

    # 白色钟圈
    d.ellipse([cx - r, cy - r, cx + r, cy + r], outline=WHITE, width=lwi)

    def hand(angle_deg, length):
        a = math.radians(angle_deg)
        x = cx + length * math.sin(a)
        y = cy - length * math.cos(a)
        d.line([cx, cy, x, y], fill=WHITE, width=lwi)
        d.ellipse([x - lw / 2, y - lw / 2, x + lw / 2, y + lw / 2], fill=WHITE)

    hand(305, r * 0.60)   # 时针（10 点位）
    hand(60, r * 0.92)    # 分针（10 分位）—— 经典 10:10 姿态
    # 中心轴
    d.ellipse([cx - lw * 0.55, cy - lw * 0.55, cx + lw * 0.55, cy + lw * 0.55], fill=WHITE)
    return img


master = draw(256)
out = os.path.join(_HERE, "app.ico")
master.save(out, format="ICO", sizes=[(s, s) for s in SIZES])
print(f"wrote {out}  sizes={SIZES}")
