#!/usr/bin/env python3
"""Generate RomboTool's app icon (1024px master PNG) from scratch with PIL.

A premium, macOS-native rounded-square ("squircle") with a diagonal blue→green
gradient, a glass sheen, and a white magnifying glass whose lens holds a lightning
bolt — "blazing-fast search". Run via ../../make-icon.sh which also builds the .icns.
"""
import math
from PIL import Image, ImageDraw, ImageFilter

SIZE = 1024
PAD = 88
RECT = (PAD, PAD, SIZE - PAD, SIZE - PAD)
RADIUS = 224

BLUE = (0x3B, 0x82, 0xF6)      # #3B82F6
CYAN = (0x38, 0xBD, 0xF8)      # #38BDF8
GREEN = (0x22, 0xC5, 0x5E)     # #22C55E


def diagonal_gradient(size, c0, c1, c2):
    """Blue (top-left) → cyan (middle) → green (bottom-right) along the diagonal.

    Rendered at low resolution (smooth gradient) and upscaled for speed.
    """
    n = 160
    small = Image.new("RGB", (n, n))
    px = small.load()
    for y in range(n):
        for x in range(n):
            t = (x + y) / (2 * (n - 1))
            if t < 0.5:
                u = t / 0.5
                col = tuple(round(c0[i] + (c1[i] - c0[i]) * u) for i in range(3))
            else:
                u = (t - 0.5) / 0.5
                col = tuple(round(c1[i] + (c2[i] - c1[i]) * u) for i in range(3))
            px[x, y] = col
    return small.resize((size, size), Image.BILINEAR)


def squircle_mask():
    mask = Image.new("L", (SIZE, SIZE), 0)
    ImageDraw.Draw(mask).rounded_rectangle(RECT, radius=RADIUS, fill=255)
    return mask


def thick_line(draw, p0, p1, width, fill):
    draw.line([p0, p1], fill=fill, width=width)
    r = width // 2
    for (x, y) in (p0, p1):
        draw.ellipse((x - r, y - r, x + r, y + r), fill=fill)


def main():
    img = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    mask = squircle_mask()

    # Gradient fill inside the squircle
    grad = diagonal_gradient(SIZE, BLUE, CYAN, GREEN).convert("RGBA")
    img.paste(grad, (0, 0), mask)

    # Very faint top sheen (flat, understated — no glossy highlight)
    sheen = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    ImageDraw.Draw(sheen).rounded_rectangle(
        (PAD, PAD, SIZE - PAD, PAD + 300), radius=RADIUS, fill=(255, 255, 255, 16))
    sheen.putalpha(Image.composite(sheen.getchannel("A"), Image.new("L", (SIZE, SIZE), 0), mask))
    img = Image.alpha_composite(img, sheen)

    # ── Magnifying glass geometry ──
    cx, cy = 452, 430
    r_out = 214
    ring_w = 72
    ring_bbox = (cx - r_out, cy - r_out, cx + r_out, cy + r_out)

    ang = math.radians(45)
    edge = (cx + r_out * math.cos(ang), cy + r_out * math.sin(ang))
    handle_end = (edge[0] + 208 * math.cos(ang), edge[1] + 208 * math.sin(ang))
    handle_w = 82

    # Faint drop shadow for the glass (subtle depth, not glossy)
    shadow = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    sdraw = ImageDraw.Draw(shadow)
    sdraw.ellipse((ring_bbox[0] + 5, ring_bbox[1] + 10, ring_bbox[2] + 5, ring_bbox[3] + 10),
                  outline=(0, 20, 40, 70), width=ring_w)
    thick_line(sdraw, (edge[0] + 5, edge[1] + 10), (handle_end[0] + 5, handle_end[1] + 10),
               handle_w, (0, 20, 40, 70))
    shadow = shadow.filter(ImageFilter.GaussianBlur(10))
    img = Image.alpha_composite(img, shadow)

    # White glass (ring + handle)
    glass = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    gdraw = ImageDraw.Draw(glass)
    thick_line(gdraw, edge, handle_end, handle_w, (255, 255, 255, 255))
    gdraw.ellipse(ring_bbox, outline=(255, 255, 255, 255), width=ring_w)

    # Lens tint: a barely-there glaze so the bolt sits on "glass"
    r_in = r_out - ring_w
    gdraw.ellipse((cx - r_in, cy - r_in, cx + r_in, cy + r_in), fill=(255, 255, 255, 12))

    # Lightning bolt inside the lens
    bolt = [
        (cx + 14, cy - 104),
        (cx - 52, cy + 14),
        (cx - 8, cy + 14),
        (cx - 30, cy + 108),
        (cx + 60, cy - 20),
        (cx + 14, cy - 20),
        (cx + 44, cy - 104),
    ]
    gdraw.polygon(bolt, fill=(255, 255, 255, 255))

    img = Image.alpha_composite(img, glass)

    img.save("icon_1024.png")
    img.resize((256, 256), Image.LANCZOS).save("icon.png")  # window icon
    print("wrote gui/Assets/icon_1024.png and icon.png")


if __name__ == "__main__":
    main()
