"""Generate a green unlock (open padlock) icon (unlock.ico) using Pillow.

Matches the style of the red lock overlay icon: a green open padlock with a
lifted shackle (gap between shackle ends and body signals "unlocked"),
transparent background, supersampled for anti-aliasing.
"""
from PIL import Image, ImageDraw

GREEN = (0x10, 0x7C, 0x10, 255)      # Windows success green
GREEN_DARK = (0x0B, 0x5E, 0x0B, 255) # shading
SHINE = (255, 255, 255, 120)


def draw_unlock(size, scale=8):
    S = size * scale
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    def P(v):
        return v * S

    # ---- Shackle (open U, lifted with a gap above the body) ----
    ring_cx = P(0.50)
    ring_cy = P(0.30)
    outer_r = P(0.24)
    inner_r = P(0.13)
    donut = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    dd = ImageDraw.Draw(donut)
    dd.ellipse([ring_cx - outer_r, ring_cy - outer_r,
                ring_cx + outer_r, ring_cy + outer_r], fill=GREEN)
    hole = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    hd = ImageDraw.Draw(hole)
    hd.ellipse([ring_cx - inner_r, ring_cy - inner_r,
                ring_cx + inner_r, ring_cy + inner_r], fill=(0, 0, 0, 255))
    # keep only the upper arc (cut below the ring ends)
    cut = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    cd = ImageDraw.Draw(cut)
    cd.rectangle([0, 0, S, P(0.41)], fill=(255, 255, 255, 255))
    ring = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ring.paste(donut, (0, 0), donut)
    ring.paste(hole, (0, 0), hole)
    ring = Image.composite(ring, Image.new("RGBA", (S, S), (0, 0, 0, 0)), cut)

    # ---- Lock body (with keyhole) ----
    body = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    bd = ImageDraw.Draw(body)
    body_box = [P(0.20), P(0.46), P(0.80), P(0.90)]
    radius = P(0.10)
    bd.rounded_rectangle(body_box, radius=radius, fill=GREEN)

    keyhole = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    kd = ImageDraw.Draw(keyhole)
    kh = P(0.09)
    kc = (P(0.50), P(0.60))
    kd.ellipse([kc[0] - kh, kc[1] - kh, kc[0] + kh, kc[1] + kh], fill=(0, 0, 0, 255))
    sw = P(0.05)
    kd.rectangle([kc[0] - sw / 2, kc[1] + kh * 0.6,
                  kc[0] + sw / 2, P(0.82)], fill=(0, 0, 0, 255))

    lock = Image.alpha_composite(ring, body)
    lock = Image.alpha_composite(lock, keyhole)

    # ---- Depth / shading ----
    shade = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    sd = ImageDraw.Draw(shade)
    sd.rounded_rectangle(body_box, radius=radius, fill=GREEN_DARK)
    grad = Image.new("L", (S, S), 0)
    gd = ImageDraw.Draw(grad)
    for y in range(S):
        t = max(0.0, min(1.0, (y / S - 0.4) / 0.6))
        gd.line([(0, y), (S, y)], fill=int(140 * t))
    lock = Image.composite(shade, lock, grad)

    hl = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    hd2 = ImageDraw.Draw(hl)
    hd2.rounded_rectangle([P(0.24), P(0.50), P(0.48), P(0.56)],
                          radius=P(0.02), fill=SHINE)
    lock = Image.alpha_composite(lock, hl)

    return lock.resize((size, size), Image.LANCZOS)


def main():
    out = r"f:\VScode\文件夹加密\FolderCrypto.ShellNative\unlock.ico"
    sizes = [16, 24, 32, 48, 64, 128, 256]
    master = draw_unlock(256)
    master.save(out, format="ICO", sizes=[(s, s) for s in sizes])
    print("saved", out)


if __name__ == "__main__":
    main()
