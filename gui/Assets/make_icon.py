#!/usr/bin/env python3
"""Generate RomboTool's app icon (1024px master PNG) from scratch with PIL.

A white lightning bolt on a green->blue rounded-square ("squircle"), matching the
"⚡ RomboTool" branding. Run via ../../make-icon.sh which also builds the .icns.
"""
from PIL import Image, ImageDraw, ImageFilter

SIZE = 1024
PAD = 100                      # transparent margin -> 824px content (macOS grid)
RECT = (PAD, PAD, SIZE - PAD, SIZE - PAD)
RADIUS = 186
TOP = (0x2B, 0xD4, 0x66)       # vibrant green  (#2BD466)
BOTTOM = (0x0A, 0x84, 0xFF)    # apple blue     (#0A84FF)


def vertical_gradient(w, h, top, bottom):
    base = Image.new("RGB", (1, h))
    for y in range(h):
        t = y / (h - 1)
        base.putpixel((0, y), tuple(round(top[i] + (bottom[i] - top[i]) * t) for i in range(3)))
    return base.resize((w, h))


def main():
    img = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))

    # Squircle mask + gradient fill
    mask = Image.new("L", (SIZE, SIZE), 0)
    ImageDraw.Draw(mask).rounded_rectangle(RECT, radius=RADIUS, fill=255)
    grad = vertical_gradient(SIZE, SIZE, TOP, BOTTOM).convert("RGBA")
    img.paste(grad, (0, 0), mask)

    # Subtle top sheen for depth
    sheen = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    sd = ImageDraw.Draw(sheen)
    sd.rounded_rectangle((PAD, PAD, SIZE - PAD, PAD + 300), radius=RADIUS, fill=(255, 255, 255, 38))
    sheen.putalpha(Image.composite(sheen.getchannel("A"), Image.new("L", (SIZE, SIZE), 0), mask))
    img = Image.alpha_composite(img, sheen)

    # Lightning bolt
    bolt = [(585, 165), (372, 560), (508, 560), (448, 868),
            (672, 470), (532, 470), (612, 165)]

    shadow = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    ImageDraw.Draw(shadow).polygon([(x + 6, y + 12) for x, y in bolt], fill=(0, 40, 20, 110))
    shadow = shadow.filter(ImageFilter.GaussianBlur(10))
    img = Image.alpha_composite(img, shadow)

    ImageDraw.Draw(img).polygon(bolt, fill=(255, 255, 255, 255))

    img.save("icon_1024.png")
    img.resize((256, 256), Image.LANCZOS).save("icon.png")  # window icon
    print("wrote gui/Assets/icon_1024.png and icon.png")


if __name__ == "__main__":
    main()
