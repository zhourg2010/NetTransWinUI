#!/usr/bin/env python3
"""量贴合缝的那道投影。

两个窗口吸在一起时，主窗右缘应当有一道向内渐深的投影，缝上是一条固定
的细线，缝右边的详情窗是干干净净的。这支尺子做三件事：

  1. 找到缝在哪 —— 一列比右邻暗一大截、而右边一片平的位置
  2. 沿缝取若干行，打出 -55..+10 的亮度剖面
  3. 和设计稿的剖面并排比，报最大偏差

用法：
    measure-seam.py 图.png [--seam X] [--rows y1,y2,...] [--baseline 设计稿.png]
"""
import sys
from PIL import Image

REACH = 50          # 往缝左边看多远
RIGHT = 10          # 往缝右边看多远
JUMP = 20           # 缝右侧回弹至少这么多灰阶
LIGHT = 225         # 窗体自身的浅底；桌面壁纸比这暗，据此把外缘排除掉



# 设计稿的投影剖面，量法见下面的 clean_ramp：取底色干净的那些行，
# 把每个位置暗下去的量除以缝上的总落差。缝左 1px 只有 22%，剩下的
# 78% 是缝上那一根 rgba(0,0,0,.16) 的硬线一次落下的 —— 这条曲线的
# 意思就是"投影是薄的，线是硬的"。
DESIGN = [
    0.0, 0.1, 0.1, 0.1, 0.6, 0.5, 0.6, 0.3, 1.0, 0.9,
    1.7, 1.6, 2.2, 2.0, 2.6, 3.0, 3.5, 3.1, 3.9, 4.0,
    4.3, 4.9, 5.3, 5.6, 5.8, 6.3, 6.7, 7.5, 7.6, 8.3,
    9.0, 8.8, 10.0, 10.4, 11.2, 11.6, 12.3, 12.8, 13.9, 14.3,
    15.2, 16.0, 16.4, 17.3, 18.0, 18.9, 19.6, 20.4, 21.3, 21.9,
    100.0,
]

# 上表量出来时，那些干净行的底色平均 248、缝上 203。归一化的分母是
# (底色 − 缝上)，而柔和投影本身是按底色成比例叠的、缝上那根线却是个
# 定值，所以换一张底色不同的图，同一套实现量出来会差几个点。下面这个
# 常数就是用来把这点漂移除掉的。
DESIGN_BG, DESIGN_SEAM = 248.0, 203.0

TOLERANCE = 5.0     # 百分点；总落差 45 级，1 个灰阶约合 2.2 点

# 缝上那根线是 rgba(0,0,0,.16)，压在详情窗自己的组底色上：242 → 203。
# 拿缝右边的底色做基准，量出来就跟主窗那边摆什么内容无关了。
EDGE_KEEP = 0.839           # 1 − .161
EDGE_SLACK = 0.012

FLAT = 2            # 缝右边允许的起伏（灰阶）

def median(values):
    v = sorted(values)
    return v[len(v) // 2]


def luma(p):
    return (p[0] * 299 + p[1] * 587 + p[2] * 114) // 1000


def find_seam(px, w, h):
    """返回最像贴合缝的那一列。"""
    band = [h * k // 10 for k in range(3, 8)]
    best, score = None, 0
    for x in range(REACH + 1, w - RIGHT - 6):
        hits = 0
        for y in band:
            here = luma(px[x, y])
            right = luma(px[x + 1, y])
            left = luma(px[x - 6, y])
            # 贴合缝两侧都是窗体：左边是主窗的浅底，右边是详情窗的浅底。
            # 窗口外缘也符合"暗一列 + 右边回弹"，但它一侧是桌面壁纸，
            # 照这条就排掉了。
            inside = median(luma(px[x - d, y]) for d in range(REACH - 10, REACH + 1))
            outside = median(luma(px[x + d, y]) for d in range(3, 14))
            if (right - here >= JUMP and left - here >= 6
                    and inside >= LIGHT and outside >= LIGHT):
                hits += 1
        if hits > score:
            best, score = x, hits
    return best if score >= len(band) - 1 else None


def ramp(line, d):
    """某处暗下去的量，占缝上总落差的百分比 —— 与底色无关的形状。"""
    bg = max(line[0:15])
    total = bg - line[REACH]
    if total <= 0:
        return 0
    return round((bg - line[d + REACH]) * 100 / total)


def clean_ramp(px, seam, w, h):
    """底色干净的行上，逐像素的归一化剖面。内容花的行一概不要 ——
    投影叠在什么上面，量出来就是什么，混进来只会把曲线搅浑。"""
    kept = []
    for y in range(60, h - 60):
        if seam - REACH < 0 or seam + 13 >= w:
            break
        line = [luma(px[seam + d, y]) for d in range(-REACH, 1)]
        plateau = line[0:8]
        if max(plateau) - min(plateau) > 1:
            continue
        bg = plateau[0]
        total = bg - line[-1]
        if total < 20:
            continue
        if any(line[i + 1] - line[i] > 1 for i in range(len(line) - 1)):
            continue
        kept.append(([(bg - v) * 100.0 / total for v in line], bg, line[-1], line))
    return kept


def check(px, seam, w, h):
    kept = clean_ramp(px, seam, w, h)
    problems = []
    if len(kept) < 10:
        return ['底色干净的行只有 %d 条，量不出剖面' % len(kept)]

    n = REACH + 1
    avg = [sum(k[0][i] for k in kept) / len(kept) for i in range(n)]
    avg_raw = [sum(k[3][i] for k in kept) / len(kept) for i in range(n)]
    bg = sum(k[1] for k in kept) / len(kept)
    seam_v = sum(k[2] for k in kept) / len(kept)

    # —— 缝上那根线 ——
    band = sorted(luma(px[seam + d, y])
                  for d in range(2, 13)
                  for y in range(h // 3, h * 2 // 3, 7))
    right_bg = band[len(band) // 2]
    keep = seam_v / right_bg
    print('  缝上一线 %.0f ÷ 缝右底色 %d = %.3f，设计 %.3f'
          % (seam_v, right_bg, keep, EDGE_KEEP))
    if abs(keep - EDGE_KEEP) > EDGE_SLACK:
        problems.append('缝上那根线 %.3f，该是 %.3f ± %.3f'
                        % (keep, EDGE_KEEP, EDGE_SLACK))

    # —— 柔和投影的形状 ——
    scale = ((bg / (bg - seam_v)) / (DESIGN_BG / (DESIGN_BG - DESIGN_SEAM))
             if bg > seam_v else 1.0)
    worst, at = 0.0, 0
    for i in range(n - 1):                      # 缝本身已经单独查过
        d = abs(avg[i] - DESIGN[i] * scale)
        if d > worst:
            worst, at = d, i - REACH
    print('  投影形状 最大偏差 %.1f 点（在缝左 %d px），容差 %.1f'
          % (worst, -at, TOLERANCE))
    print('  缝左 1px 本机 %.1f%%，设计 %.1f%% —— 投影只占总落差的两成，'
          % (avg[n - 2], DESIGN[n - 2] * scale))
    print('           八成是缝上那根线一次落下的')
    if worst > TOLERANCE:
        problems.append('投影形状在缝左 %d px 偏 %.1f 点' % (-at, worst))

    # —— 缝右边应当是干净的 ——
    swing = max(band) - min(band)
    print('  缝右 2..12px 起伏 %d 灰阶，设计那边是平的' % swing)
    # 出问题的时候光有百分比不够用，把缝附近的原始灰阶也打出来 ——
    # 不然每查一次都要再发一版才看得到像素。
    print('  缝左 12..0 原始灰阶 %s  底色 %.0f'
          % (' '.join('%d' % avg_raw[i] for i in range(n - 13, n)), bg))
    if swing > FLAT:
        problems.append('缝右边不平（起伏 %d 灰阶）：详情窗自己的边该是干净的' % swing)
    return problems


def profile(px, seam, rows):
    out = {}
    for y in rows:
        out[y] = [luma(px[seam + d, y]) for d in range(-REACH, RIGHT + 1)]
    return out


def main(argv):
    path = argv[0]
    seam = None
    rows = None
    baseline = None
    strict = False
    i = 1
    while i < len(argv):
        if argv[i] == '--seam':
            seam = int(argv[i + 1]); i += 2
        elif argv[i] == '--rows':
            rows = [int(v) for v in argv[i + 1].split(',')]; i += 2
        elif argv[i] == '--check':
            strict = True; i += 1
        elif argv[i] == '--baseline':
            baseline = argv[i + 1]; i += 2
        else:
            raise SystemExit('不认识的参数：' + argv[i])

    im = Image.open(path).convert('RGB')
    w, h = im.size
    px = im.load()
    if seam is None:
        seam = find_seam(px, w, h)
        if seam is None:
            print('没找到贴合缝：%s（%dx%d）' % (path, w, h))
            return 1
    if rows is None:
        rows = [h * 3 // 10, h // 2, h * 7 // 10]

    print('%s  %dx%d  缝在 x=%d' % (path, w, h, seam))

    if strict:
        problems = check(px, seam, w, h)
        for line in problems:
            print('  ✗ ' + line)
        print('贴合缝：%s' % ('不合格' if problems else '合格'))
        return 1 if problems else 0
    prof = profile(px, seam, rows)
    for y in rows:
        line = prof[y]
        cells = ' '.join('%+d:%d' % (d, line[d + REACH])
                         for d in range(-REACH, RIGHT + 1, 5))
        print('  y=%-5d %s' % (y, cells))
        # 背景取投影够不到的那一段里最亮的，避免正好扎在某个控件上
        flat = max(line[0:15])
        print('        缝上=%d  背景=%d  落差=%d' % (line[REACH], flat, flat - line[REACH]))

    if baseline:
        bi = Image.open(baseline).convert('RGB')
        bpx = bi.load()
        bw, bh = bi.size
        bseam = find_seam(bpx, bw, bh)
        if bseam is None:
            print('设计稿里没找到缝：' + baseline)
            return 1
        brows = [bh * 3 // 10, bh // 2, bh * 7 // 10]
        bprof = profile(bpx, bseam, brows)
        print('\n对照 %s（缝在 x=%d）' % (baseline, bseam))
        print('  投影是叠在内容上的，底色越白落差越大，绝对灰阶没法直接比。')
        print('  这里比的是形状：每个位置暗下去的量，占缝上总落差的百分之几。')
        print('  偏移   本机   设计   差')
        worst = 0
        for d in range(-REACH, 1, 5):
            a = ramp(prof[rows[1]], d)
            b = ramp(bprof[brows[1]], d)
            worst = max(worst, abs(a - b))
            print('  %+4d  %4d%%  %4d%%  %+4d' % (d, a, b, a - b))
        print('  形状最大偏差 %d 个百分点' % worst)
        print('  缝上一线   本机 %d   设计 %d' % (prof[rows[1]][REACH], bprof[brows[1]][REACH]))
        print('  缝右首像素 本机 %d   设计 %d（详情窗那面应当是干净的）'
              % (prof[rows[1]][REACH + 3], bprof[brows[1]][REACH + 3]))
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
