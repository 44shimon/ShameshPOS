"""Draws the Shmesh POS app icon and writes a multi-size .ico plus a preview PNG.

Usage (needs Pillow): python scripts/make-icon.py src/ShmeshPOS/Resources/ShmeshPOS.ico icon-preview.png
"""
import sys
from PIL import Image, ImageDraw, ImageFont

ico_path, preview_path = sys.argv[1], sys.argv[2]
BLUE_TOP = (59, 130, 246)     # lighter blue at the top of the tile
BLUE_BOTTOM = (29, 78, 216)   # PrimaryDark #1D4ED8
BLUE = (37, 99, 235)          # Primary #2563EB
WHITE = (255, 255, 255)
FONT = "C:/Windows/Fonts/seguibl.ttf"  # Segoe UI Black
S = 1024                      # master size; each icon size is drawn at 4x and scaled down


def tile(size):
    """Rounded square with a vertical blue gradient."""
    grad = Image.new("RGBA", (size, size))
    d = ImageDraw.Draw(grad)
    for y in range(size):
        t = y / (size - 1)
        d.line([(0, y), (size, y)], fill=tuple(round(a + (b - a) * t) for a, b in zip(BLUE_TOP, BLUE_BOTTOM)) + (255,))
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, size - 1, size - 1], radius=round(size * 0.22), fill=255)
    out = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    out.paste(grad, (0, 0), mask)
    return out


def centered_text(draw, box, text, font, fill):
    l, t, r, b = draw.textbbox((0, 0), text, font=font)
    x = (box[0] + box[2]) / 2 - (l + r) / 2
    y = (box[1] + box[3]) / 2 - (t + b) / 2
    draw.text((x, y), text, font=font, fill=fill)


def detailed(size):
    """Receipt with a big S and two printed lines, for 32 px and up."""
    img = tile(size)
    d = ImageDraw.Draw(img)
    u = size / 100
    left, right, top, bottom = 24 * u, 76 * u, 14 * u, 80 * u
    teeth, depth = 6, 6 * u
    # Receipt body with a torn (zigzag) bottom edge.
    pts = [(left, top), (right, top), (right, bottom)]
    w = (right - left) / teeth
    for i in range(teeth):
        x = right - i * w
        pts += [(x - w / 2, bottom + depth), (x - w, bottom)]
    # Soft shadow, then the paper.
    shadow = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    ImageDraw.Draw(shadow).polygon([(x, y + 2.5 * u) for x, y in pts], fill=(15, 23, 42, 70))
    img.alpha_composite(shadow)
    d.polygon(pts, fill=WHITE)
    d.rounded_rectangle([left, top, right, top + 8 * u], radius=3 * u, fill=WHITE)
    # Big S near the top of the receipt.
    centered_text(d, (left, top + 2 * u, right, 58 * u), "S", ImageFont.truetype(FONT, round(44 * u)), BLUE)
    # Two "printed" lines underneath.
    for y, x2 in ((64 * u, 68 * u), (72 * u, 58 * u)):
        d.rounded_rectangle([32 * u, y, x2, y + 4 * u], radius=2 * u, fill=(147, 197, 253))
    return img


def simple(size):
    """Just a white S on the tile, so 16 and 24 px stay readable."""
    img = tile(size)
    centered_text(ImageDraw.Draw(img), (0, 0, size, size * 0.98), "S", ImageFont.truetype(FONT, round(size * 0.78)), WHITE)
    return img


sizes = [16, 24, 32, 48, 64, 128, 256]
frames = []
for px in sizes:
    big = (simple if px <= 24 else detailed)(px * 4)
    frames.append(big.resize((px, px), Image.LANCZOS))

frames[-1].save(ico_path, format="ICO", sizes=[(p, p) for p in sizes], append_images=frames[:-1])

# Preview: every size side by side on light and dark backgrounds.
width = sum(sizes) + 20 * (len(sizes) + 1)
preview = Image.new("RGBA", (width, 2 * 256 + 60), (255, 255, 255, 255))
ImageDraw.Draw(preview).rectangle([0, 256 + 30, width, 2 * 256 + 60], fill=(15, 23, 42, 255))
x = 20
for px, f in zip(sizes, frames):
    preview.alpha_composite(f, (x, 10 + (256 - px)))
    preview.alpha_composite(f, (x, 256 + 40 + (256 - px)))
    x += px + 20
preview.save(preview_path)
print("ok")
