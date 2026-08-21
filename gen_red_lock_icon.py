"""Generate a red padlock icon (overlay-lock.ico) using Pillow.

Draws a crisp red lock with a transparent background, supersampled for
anti-aliasing, and saves an .ico containing common sizes.
"""
import math
from PIL import Image, ImageDraw

RED = (0xE8, 0x11, 0x23, 255)       # Windows error red
RED_DARK = (0xC5, 0x0D, 0x1D, 255)  # shading for depth
SHINE = (255, 255, 255, 120)        # subtle highlight


def rounded_rect(draw, box, radius, fill):
    draw.rounded_rectangle(box, radius=radius, fill=fill)


def draw_lock(size, scale=8):
    """Render a red padlock at the given pixel size (supersampled by `scale`)."""
    S = size * scale
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    def P(v):
        return v * S

    # ---- Shackle (the loop at the top) ----
    # Outer and inner radii of the ring so it reads as a thick U.
    ring_cx = P(0.50)
    ring_cy = P(0.34)
    outer_r = P(0.26)
    inner_r = P(0.15)
    # Draw ring as two filled circles minus the inner circle (a donut), then
    # cut off the bottom half so only the open shackle remains.
    donut = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    dd = ImageDraw.Draw(donut)
    dd.ellipse([ring_cx - outer_r, ring_cy - outer_r,
                ring_cx + outer_r, ring_cy + outer_r], fill=RED)
    # carve out the hole
    hole = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    hd = ImageDraw.Draw(hole)
    hd.ellipse([ring_cx - inner_r, ring_cy - inner_r,
                ring_cx + inner_r, ring_cy + inner_r], fill=(0, 0, 0, 255))
    # clip the donut to keep only the upper shackle (bottom cut at lock top)
    cut = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    cd = ImageDraw.Draw(cut)
    cd.rectangle([0, 0, S, P(0.46)], fill=(255, 255, 255, 255))
    ring = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ring.paste(donut, (0, 0), donut)
    ring.paste(hole, (0, 0), hole)
    ring = Image.composite(ring, Image.new("RGBA", (S, S), (0, 0, 0, 0)), cut)

    # ---- Lock body ----
    body = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    bd = ImageDraw.Draw(body)
    body_box = [P(0.20), P(0.42), P(0.80), P(0.88)]
    radius = P(0.10)
    bd.rounded_rectangle(body_box, radius=radius, fill=RED)

    # ---- Keyhole ----
    keyhole = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    kd = ImageDraw.Draw(keyhole)
    kh = P(0.10)
    kc = (P(0.50), P(0.575))
    kd.ellipse([kc[0] - kh, kc[1] - kh, kc[0] + kh, kc[1] + kh], fill=(0, 0, 0, 255))
    # stem below the circle
    sw = P(0.055)
    kd.rectangle([kc[0] - sw / 2, kc[1] + kh * 0.6,
                  kc[0] + sw / 2, P(0.80)], fill=(0, 0, 0, 255))

    # ---- Compose ----
    lock = Image.alpha_composite(ring, body)
    lock = Image.alpha_composite(lock, keyhole)

    # ---- Depth / shading (darken lower-right, highlight top-left) ----
    shade = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    sd = ImageDraw.Draw(shade)
    sd.rounded_rectangle(body_box, radius=radius, fill=RED_DARK)
    # gradient mask: only darken bottom portion
    grad = Image.new("L", (S, S), 0)
    gd = ImageDraw.Draw(grad)
    for y in range(S):
        t = (y / S - 0.4) / 0.6
        t = max(0.0, min(1.0, t))
        alpha = int(140 * t)
        gd.line([(0, y), (S, y)], fill=alpha)
    lock = Image.composite(shade, lock, grad)

    # highlight on upper-left of body
    hl = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    hd2 = ImageDraw.Draw(hl)
    hd2.rounded_rectangle([P(0.24), P(0.46), P(0.48), P(0.52)],
                          radius=P(0.02), fill=SHINE)
    lock = Image.alpha_composite(lock, hl)

    lock = lock.resize((size, size), Image.LANCZOS)
    return lock


def main():
    out = r"f:\VScode\文件夹加密\FolderCrypto.ShellNative\overlay-lock.ico"
    sizes = [16, 24, 32, 48, 64, 128, 256]
    # 渲染最大尺寸（带超采样抗锯齿），再由 Pillow 缩放到各尺寸并写入单个多尺寸 .ico
    master = draw_lock(256)
    master.save(out, format="ICO", sizes=[(s, s) for s in sizes])
    print("saved", out)


if __name__ == "__main__":
    main()
