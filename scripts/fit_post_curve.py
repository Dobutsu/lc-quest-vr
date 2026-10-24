"""HDRP's post-processing of the main camera as one tone curve, measured from paired reference renders of the same
views (scripts/reference-render.sh, with and without -neutral): median output for each input level, per channel.
The table goes into HdrpBridge as a URP user LUT (the game's default profile: auto exposure, contrast, custom tonemap).
Usage: <venv>/bin/python scripts/fit_post_curve.py <neutral dir> <full dir> <view> [<view> ...]
"""

import math
import sys

from PIL import Image


def linear(c: float) -> float:
    c /= 255
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


def main() -> int:
    neutral_dir, full_dir, views = sys.argv[1], sys.argv[2], sys.argv[3:]
    bins: dict[float, list[float]] = {}
    for view in views:
        a = Image.open(f"{neutral_dir}/{view}.png").convert("RGB").load()
        b = Image.open(f"{full_dir}/{view}.png").convert("RGB")
        w, h = b.size
        b = b.load()
        for y in range(0, h, 2):
            for x in range(0, w, 2):
                for ch in range(3):
                    n, f = linear(a[x, y][ch]), linear(b[x, y][ch])
                    if n > 0:
                        bins.setdefault(round(math.log2(n) * 4) / 4, []).append(f)
    points = []
    for k in sorted(bins):
        values = sorted(bins[k])
        if len(values) >= 50:
            points.append((2 ** k, values[len(values) // 2]))
    # Monotonic, as a tone curve must be
    for i in range(1, len(points)):
        points[i] = (points[i][0], max(points[i][1], points[i - 1][1]))
    print("// input -> output, linear")
    print("private static readonly Vector2[] MeasuredPostCurve =\n{")
    print(",\n".join(f"    new Vector2({x:.5f}f, {y:.5f}f)" for x, y in points))
    print("};")
    return 0


if __name__ == "__main__":
    sys.exit(main())
