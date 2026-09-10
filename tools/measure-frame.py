#!/usr/bin/env python3
"""和设计稿量同一把尺子: measure the frame the app actually drew.

Takes a screenshot of the running app -- the whole desktop is fine -- and
reports where the title bar, the task rows and the toolbar landed, in window
coordinates, then checks them against the handoff.

The numbers it checks against come from the handoff itself: its README
("536 x 680 px", "标题栏 .nav 高 44px", "工具栏 .bar 高 49px") and, for what
the README leaves to the CSS, from measuring its own rendered prototype
(.row 62px).

Usage:
    python3 tools/measure-frame.py screenshots/desktop.png [--check]

Without --check it only reports, which is what you want when the layout has
moved on purpose and you need to see where to.
"""
import sys

from PIL import Image

# The handoff's frame, in DIPs. The runner renders at 100%, so these are pixels.
FRAME_WIDTH = 536
FRAME_HEIGHT = 680
NAV_HEIGHT = 44
BAR_HEIGHT = 49
ROW_HEIGHT = 62
SEG_TOP = 44      # the segmented control butts straight up under the title bar
SEG_HEIGHT = 32

TOLERANCE = 1

# How far in from the window's left edge to run the vertical probe. The frame is
# group grey there from the title bar to the toolbar: the segmented control and
# the task card are both inset 16px, so nothing paints over it and every band it
# crosses is a real full-width band rather than a glyph.
PROBE = 6


def is_light(pixel):
    """A window surface: near-neutral and bright. The desktop behind it is a blue gradient.

    The threshold has to be low enough to include a .5px separator (214) as well
    as the surfaces either side of it (242 and 248): the title bar's rule and the
    toolbar's run the full width of the frame, so a stricter test cuts the window
    into three and measures only the middle.
    """
    red, green, blue = pixel[:3]
    return abs(red - green) < 10 and abs(green - blue) < 12 and red > 205


def is_hairline(colour):
    """A .5px separator: neutral, and darker than any surface."""
    return max(colour) - min(colour) < 12 and colour[0] < 226


def span_through(image, x, y):
    """The run of window surface down column x that contains row y.

    Through, not tallest: a horizontal band belongs to whatever is at its own
    row. Taking the tallest run instead let the taskbar borrow the app's height.
    """
    px = image.load()
    _, height = image.size

    if not is_light(px[x, y]):
        return y, y

    top = y
    while top > 0 and is_light(px[x, top - 1]):
        top -= 1

    bottom = y
    while bottom + 1 < height and is_light(px[x, bottom + 1]):
        bottom += 1

    return top, bottom + 1


def find_frame(image):
    """The docked pair: the largest rectangle of window surface on the desktop.

    Largest by area, not by width -- the taskbar is a wider band of very nearly
    the same grey, and it is what a widest-run search finds instead.
    """
    px = image.load()
    width, height = image.size

    seen = set()
    best = None

    for y in range(0, height, 4):
        start = None
        for x in range(width + 1):
            if x < width and is_light(px[x, y]):
                if start is None:
                    start = x
            else:
                if start is not None and x - start > 400 and (start, x) not in seen:
                    seen.add((start, x))
                    top, bottom = span_through(image, start + PROBE, y)
                    area = (x - start) * (bottom - top)
                    if best is None or area > best[0]:
                        best = (area, start, x, top, bottom)
                start = None

    if best is None:
        raise SystemExit("截图里没有窗口 —— 整张图都是背景")

    _, left, right, top, bottom = best

    # The rough box is short by however many pixels the 16px corner radius
    # anti-aliases away. Refine it away from the corners: the pair's outer edges
    # are square at half height, and the bonded seam is square top and bottom.
    middle = (top + bottom) // 2
    while left > 0 and is_light(px[left - 1, middle]):
        left -= 1
    while right < width and is_light(px[right, middle]):
        right += 1

    # Just past the seam, not just before it: the task frame draws the bonded
    # neighbour's shadow across its own last 40px, and a probe inside that
    # gradient reads too dark to count as window surface at all — which
    # measured the window as 587px tall. The inspector's side of the seam is
    # flat, and square-cornered because it is bonded, so it is the one column
    # that is neither rounded nor shadowed.
    seam = left + FRAME_WIDTH + 4
    if 0 <= seam < width:
        top, bottom = span_through(image, seam, middle)

    return left, right, top, bottom


def bands(image, x, top, bottom):
    """Every colour change down one column, as (offset from the window top, colour)."""
    px = image.load()
    out = []
    previous = None
    for y in range(top, bottom):
        colour = px[x, y][:3]
        if colour != previous:
            out.append((y - top, colour))
            previous = colour
    return out


def seg_band(image, left, top, bottom):
    """The segmented control's track: the one wide band of fill grey up top.

    Its offset is worth checking on its own -- an 11px top margin that the CSS
    does not have pushed everything below it down by 11px in the inspector, and
    nothing else in this file would have noticed.
    """
    px = image.load()
    start = left + 24
    end = left + FRAME_WIDTH - 24

    rows = []
    for y in range(top, min(top + 140, bottom)):
        hits = sum(1 for x in range(start, end, 3) if px[x, y][:3] == (228, 228, 233))
        if hits > (end - start) // 3 * 0.5:
            rows.append(y - top)

    if not rows:
        return None, None
    return rows[0], rows[-1] - rows[0] + 1


def separators(image, left, top, bottom):
    """Rows where the task card draws a divider between two rows.

    Found across the card rather than down a column: the divider is inset to
    clear the icon tile, and the column that would cross it also crosses the
    file name.
    """
    px = image.load()
    start = left + 80

    # Stop short of the bonded edge: the shadow there drags the surface down
    # near the hairline range and there is nothing to measure in it anyway.
    end = left + FRAME_WIDTH - 48

    found = []
    for y in range(top, bottom):
        hits = sum(1 for x in range(start, end, 3) if is_hairline(px[x, y][:3]))
        if hits > (end - start) // 3 * 0.8:
            found.append(y - top)

    # A 1px line can straddle two rows of pixels; keep one offset per line.
    merged = []
    for offset in found:
        if not merged or offset - merged[-1] > 3:
            merged.append(offset)
    return merged


def main():
    if len(sys.argv) < 2:
        raise SystemExit(__doc__)

    check = "--check" in sys.argv
    image = Image.open(sys.argv[1]).convert("RGB")
    left, right, top, bottom = find_frame(image)
    height = bottom - top

    print(f"截图 {image.size[0]}x{image.size[1]}")
    print(f"窗口对 x={left}..{right}  宽 {right - left}")
    print(f"上下   y={top}..{bottom}  高 {height}")
    print()

    problems = []

    def expect(name, actual, wanted):
        ok = actual is not None and abs(actual - wanted) <= TOLERANCE
        shown = "找不到" if actual is None else str(actual)
        print(f"{'ok  ' if ok else 'FAIL'} {name:<22} {shown:>7}  应为 {wanted}")
        if not ok:
            problems.append(f"{name}: {shown}，应为 {wanted}")

    expect("两窗合起来的宽度", right - left, FRAME_WIDTH * 2)
    expect("窗口高度", height, FRAME_HEIGHT)

    for index, name in enumerate(("主窗", "详情窗")):
        edge = left + index * FRAME_WIDTH
        print()
        print(f"── {name} ──")

        marks = bands(image, edge + PROBE, top, bottom)
        for offset, colour in marks[:12]:
            print(f"  {offset:4d}  rgb{colour}")
        if len(marks) > 12:
            print(f"  … 还有 {len(marks) - 12} 处变化")

        rules = separators(image, edge, top, bottom)
        print(f"  分隔线 @ {rules}")

        nav_rule = next((offset for offset in rules if 20 < offset < 70), None)
        expect(f"{name}标题栏高度", None if nav_rule is None else nav_rule + 1, NAV_HEIGHT)

        seg_top, seg_height = seg_band(image, edge, top, bottom)
        expect(f"{name}分段控件位置", seg_top, SEG_TOP)
        expect(f"{name}分段控件高度", seg_height, SEG_HEIGHT)

        # Only the task frame has a toolbar and task rows.
        if index != 0:
            continue

        bar_rule = next((offset for offset in reversed(rules) if offset > height * 3 // 4), None)
        expect("工具栏高度", None if bar_rule is None else height - bar_rule, BAR_HEIGHT)

        dividers = [offset for offset in rules if offset not in (nav_rule, bar_rule)]
        gaps = [b - a for a, b in zip(dividers, dividers[1:])]
        pitch = max(set(gaps), key=gaps.count) if gaps else None
        expect("任务行高", pitch, ROW_HEIGHT)

    if check and problems:
        print()
        print("和设计稿对不上：")
        for problem in problems:
            print("  - " + problem)
        return 1

    return 0


if __name__ == "__main__":
    sys.exit(main())
