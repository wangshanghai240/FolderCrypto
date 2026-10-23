# -*- coding: utf-8 -*-
"""生成中性「钥匙」图标 toggle-key.ico。

用于「加密 / 解密」共享右键菜单项的图标。

为什么需要中性图标：
  菜单项合并为一个之后，注册表里的 Icon 只能指向一张固定图片，
  无法随目标状态（已加密 / 未加密）动态变化。
  用红锁会让人误以为「一定是加密」，用绿锁则相反，
  因此改用与状态无关的钥匙图形。

输出：
  FolderCrypto.ShellNative/toggle-key.ico
尺寸：16/20/24/32/40/48/64/128/256（32 位 ARGB）
"""

import os
from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "FolderCrypto.ShellNative", "toggle-key.ico")

SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]

FILL = (222, 226, 232, 255)   # 浅灰主体
LINE = (38, 40, 46, 255)      # 深色描边，保证深浅主题下都清晰


def draw(size):
    S = size * 16                      # 超采样后缩放，边缘更平滑
    ss = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(ss)
    lw = max(2, int(S * 0.035))

    cx = S * 0.5
    ring_r = S * 0.215
    ring_cy = S * 0.255

    # 钥匙环
    d.ellipse([cx - ring_r, ring_cy - ring_r, cx + ring_r, ring_cy + ring_r],
              fill=FILL, outline=LINE, width=lw)

    # 钥匙杆
    shaft_w = S * 0.115
    shaft_top = ring_cy + ring_r * 0.62
    shaft_bot = S * 0.905
    d.rectangle([cx - shaft_w / 2, shaft_top, cx + shaft_w / 2, shaft_bot],
                fill=FILL, outline=LINE, width=lw)

    # 两个齿
    t1_y = S * 0.665
    d.rectangle([cx, t1_y, cx + S * 0.185, t1_y + S * 0.085],
                fill=FILL, outline=LINE, width=lw)
    t2_y = S * 0.800
    d.rectangle([cx, t2_y, cx + S * 0.115, t2_y + S * 0.080],
                fill=FILL, outline=LINE, width=lw)

    # 挖空环内孔
    hole_r = ring_r * 0.46
    d.ellipse([cx - hole_r + 1, ring_cy - hole_r + 1,
               cx + hole_r - 1, ring_cy + hole_r - 1], fill=(0, 0, 0, 0))

    return ss.resize((size, size), Image.LANCZOS)


def main():
    frames = [draw(s) for s in SIZES]
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    frames[0].save(OUT, format="ICO",
                   sizes=[(s, s) for s in SIZES],
                   append_images=frames[1:])
    print(f"toggle-key.ico -> {OUT} ({os.path.getsize(OUT)} bytes)")


if __name__ == "__main__":
    main()
