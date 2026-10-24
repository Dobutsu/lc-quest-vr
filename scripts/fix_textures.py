"""Texture import settings the Quest build needs, written into the exported .meta files.

3D textures (textureShape 8: the local volumetric fog masks, eye caustics LUT) are imported uncompressed for Android:
ETC2/EAC is not a 3D texture format on every GPU (the Mac editor's Metal refuses to create one and crashes when a moon
scene loads), and they are small (up to 64^3).
Usage: <venv>/bin/python scripts/fix_textures.py <unity project>
"""

import re
import sys
from pathlib import Path

ANDROID = """  - serializedVersion: 3
    buildTarget: Android
    maxTextureSize: 2048
    resizeAlgorithm: 0
    textureFormat: 4
    textureCompression: 0
    compressionQuality: 50
    crunchedCompression: 0
    allowsAlphaSplitting: 0
    overridden: 1
    ignorePlatformSupport: 0
    androidETC2FallbackOverride: 0
    forceMaximumCompressionQuality_BC6H_BC7: 0
"""


def main() -> int:
    assets = Path(sys.argv[1]) / "Assets"
    fixed = 0
    for meta in assets.rglob("*.meta"):
        text = meta.read_text(errors="surrogateescape")
        if "  textureShape: 8\n" not in text or "buildTarget: Android" in text:
            continue
        updated = re.sub(r"(  platformSettings:\n)", r"\1" + ANDROID.replace("\\", "\\\\"), text, count=1)
        if updated != text:
            meta.write_text(updated, errors="surrogateescape", newline="\n")
            fixed += 1
    print(f"3D textures uncompressed on Android: {fixed}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
