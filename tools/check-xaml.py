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

        for m in re.finditer(r'<PathGeometry[^>]*\sFigures="[^"]*[A-Za-z][^"]*"', text):
            problems.append(
                f"{name}: PathGeometry Figures=\"M …\" — Figures is a "
                f"PathFigureCollection; a path string only converts at Path.Data"
            )

        for m in re.finditer(r"\{(?:StaticResource|ThemeResource)\s+([^}]+)\}", text):
            key = m.group(1).strip()
            if key not in defined:
                problems.append(f"{name}: {{StaticResource {key}}} — no x:Key defines it")

    for problem in problems:
        print(problem)

    print(f"\n{len(files)} XAML files, {len(defined)} keys, {len(problems)} problems")

    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
