#!/usr/bin/env python3
"""
XAML checks the compiler does not do.

The XAML compiler is happy with a StaticResource whose key does not exist, and
with an abstract type declared as a resource. Both fail at runtime, when the
window is being built, as "XAML parsing failed" with no line number -- and for
a resource dictionary that everything merges, that means every window in the
app. These are cheap to check from the markup itself, so they run in CI on
Linux rather than being found by a person who downloaded the build.
"""

import glob
import os
import re
import sys
import xml.etree.ElementTree as ElementTree


def markup(path: str) -> str:
    """The file with its comments removed: prose about a mistake is not the mistake."""
    return re.sub(r"<!--.*?-->", "", open(path, encoding="utf-8").read(), flags=re.S)

# Types that cannot be instantiated from markup: WinUI has no type converter
# standing behind them the way WPF does.
ABSTRACT = {"Geometry", "Brush", "Transform", "Shape", "Timeline", "Animation"}

ROOT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "NetTrans")


def main() -> int:
    files = sorted(glob.glob(os.path.join(ROOT, "**", "*.xaml"), recursive=True))
    if not files:
        print("no XAML found", file=sys.stderr)
        return 1

    defined: set[str] = set()
    problems: list[str] = []

    for path in files:
        defined |= set(re.findall(r'x:Key="([^"]+)"', markup(path)))

    for path in files:
        text = markup(path)
        name = os.path.relpath(path, ROOT)

        for kind in ABSTRACT:
            for m in re.finditer(rf'<{kind}\s+x:Key="([^"]+)"', text):
                problems.append(
                    f'{name}: <{kind} x:Key="{m.group(1)}"> — {kind} is abstract; '
                    f"use a concrete type (PathGeometry, SolidColorBrush, …)"
                )

        # 用户看得见的地方不该再出现旧名字。命名空间和程序集仍叫
        # NetTrans.*，那是内部标识；Text/PlaceholderText/ToolTip 这些是
        # 摆在人眼前的字。改名那一版就漏掉了种子 sheet 里 PT 白名单那句，
        # 一直发到 v0.1.13 才被截图看出来。
        for m in re.finditer(
                r'(?:Text|PlaceholderText|Content|Header|Label|'
                r'ToolTipService\.ToolTip)="([^"]*NetTrans[^"]*)"', text):
            problems.append(
                f'{name}: "{m.group(1)}" — 界面上的字还叫 NetTrans，应为 netX'
            )

        for m in re.finditer(r'<PathGeometry[^>]*\sFigures="[^"]*[A-Za-z][^"]*"', text):
            problems.append(
                f"{name}: PathGeometry Figures=\"M …\" — Figures is a "
                f"PathFigureCollection; a path string only converts at Path.Data"
            )

        for m in re.finditer(r"\{(?:StaticResource|ThemeResource)\s+([^}]+)\}", text):
            key = m.group(1).strip()
            if key not in defined:
                problems.append(f"{name}: {{StaticResource {key}}} — no x:Key defines it")

        # Well-formed as XML at all. A tag closed in the wrong place is not a
        # key or a type error, so nothing above sees it; the XAML compiler
        # reports it as "Duplication assignment to the 'Body' property", which
        # names neither the line nor the tag that actually moved. Costs a
        # Windows build to find and a millisecond to catch.
        try:
            ElementTree.parse(path)
        except ElementTree.ParseError as broken:
            problems.append(f"{name}: 不是合法的 XML — {broken}")

        # XML forbids "--" inside a comment, and the XAML compiler reports it as
        # "Xaml Internal Error WMC9999" against a file in the SDK rather than
        # against the file that has it. An em dash in a sentence is the way
        # anyone writes into that trap.
        raw = open(path, encoding="utf-8").read()
        for m in re.finditer(r"<!--(.*?)-->", raw, re.S):
            if "--" in m.group(1):
                line = raw[: m.start()].count("\n") + 1
                problems.append(
                    f"{name}:{line}: 注释里有 '--' — XML 不允许，编译器会报成 WMC9999"
                )

    for problem in problems:
        print(problem)

    print(f"\n{len(files)} XAML files, {len(defined)} keys, {len(problems)} problems")

    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
