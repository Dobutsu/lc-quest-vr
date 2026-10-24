"""Mean linear luminance of image regions, reference (HDRP) against ours (URP): the ratio says how far off we are.
Usage: <venv>/bin/python scripts/compare_images.py <reference.png> <ours.png> [--brief] [name:x0,y0,x1,y1 ...]
--brief: one line, the ratios and their geometric mean
Regions are in pixels of the 860x520 views; default: a 4x3 grid.
"""

import math
import sys

from PIL import Image


def linear(c: float) -> float:
    c /= 255
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


def mean_luminance(img: Image.Image, box: tuple[int, int, int, int]) -> float:
    region = img.crop(box).convert("RGB")
    pixels = list(region.getdata())
    return sum(0.2126 * linear(r) + 0.7152 * linear(g) + 0.0722 * linear(b) for r, g, b in pixels) / max(1, len(pixels))


def main() -> int:
    brief = "--brief" in sys.argv
    argv = [a for a in sys.argv if a != "--brief"]
    ref, ours = Image.open(argv[1]), Image.open(argv[2])
    regions = {}
    for spec in argv[3:]:
        name, coords = spec.split(":")
        regions[name] = tuple(int(v) for v in coords.split(","))
    if not regions:
        w, h = ref.size
        for j in range(3):
            for i in range(4):
                regions[f"r{j}c{i}"] = (i * w // 4, j * h // 3, (i + 1) * w // 4, (j + 1) * h // 3)
    ratios = []
    for name, box in regions.items():
        a, b = mean_luminance(ref, box), mean_luminance(ours, box)
        ratio = a / b if b > 0 else float("inf")
        ratios.append(ratio)
        if not brief:
            print(f"{name:10} ref {a:.4f}  ours {b:.4f}  ratio ref/ours {ratio:.2f}")
    if brief:
        finite = [r for r in ratios if 0 < r < float("inf")]
        gmean = math.exp(sum(math.log(r) for r in finite) / len(finite)) if finite else float("nan")
        print(" ".join(f"{r:.2f}" for r in ratios) + f"  | mean {gmean:.2f}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
