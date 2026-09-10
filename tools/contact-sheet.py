#!/usr/bin/env python3
"""One sheet with every screen on it, in the order the walk took them.

Twenty separate PNGs are twenty clicks. A contact sheet is one look, which is
what deciding "does this app look right" actually needs.

Usage:
    python3 tools/contact-sheet.py screenshots/screens out.png
"""
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

COLUMNS = 5
GAP = 22
LABEL = 26
GROUND = (232, 233, 238)
INK = (40, 40, 45)

FONTS = [
    "/usr/share/fonts/truetype/wqy/wqy-zenhei.ttc",
    "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
]


def font(size):
    for path in FONTS:
        if Path(path).exists():
            return ImageFont.truetype(path, size)
    return ImageFont.load_default()


def main():
    if len(sys.argv) < 3:
        raise SystemExit(__doc__)

    source = Path(sys.argv[1])
    shots = sorted(source.glob("*.png"))
    if not shots:
        raise SystemExit(f"{source} 里没有 PNG")

    images = [(path.stem, Image.open(path).convert("RGB")) for path in shots]

    # One cell fits the largest screen; the island is a fraction of a frame and
    # is centred in its cell rather than blown up to fill it.
    cell_w = max(image.width for _, image in images)
    cell_h = max(image.height for _, image in images)

    rows = (len(images) + COLUMNS - 1) // COLUMNS
    width = GAP + COLUMNS * (cell_w + GAP)
    height = GAP + rows * (LABEL + cell_h + GAP)

    sheet = Image.new("RGB", (width, height), GROUND)
    draw = ImageDraw.Draw(sheet)
    label = font(17)

    for index, (name, image) in enumerate(images):
        column, row = index % COLUMNS, index // COLUMNS
        x = GAP + column * (cell_w + GAP)
        y = GAP + row * (LABEL + cell_h + GAP)

        draw.text((x, y), name, font=label, fill=INK)
        sheet.paste(image, (x + (cell_w - image.width) // 2, y + LABEL))

    sheet.save(sys.argv[2])
    print(f"{len(images)} 屏 → {sys.argv[2]}  {sheet.width}x{sheet.height}")


if __name__ == "__main__":
    main()
