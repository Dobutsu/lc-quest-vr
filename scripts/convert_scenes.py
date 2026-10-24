"""HDRP scene data that URP reads elsewhere, rewritten in the scene and prefab YAML: light intensities and camera clears.

Lights: from HDRP's physical units to URP intensities.

Under HDRP a Light's own m_Intensity already holds the physical value HDRP renders with: candela for point and spot
lights (HDAdditionalLightData keeps the value as the artist entered it, e.g. lumen), lux for directional lights. HDRP
then multiplies everything by the camera exposure, e = 1 / (1.2 * 2^EV100), and its Lambert diffuse divides by pi;
URP's does not, and has no exposure. So a URP light that lights a surface the same way has
    intensity = I_hdrp * lightDimmer * e / pi
The game's main volume profiles use automatic exposure clamped to EV100 1...1.6 (Exposure_0/_3), hence EXPOSURE_EV100.
The moons' shadowless directional fill lights ("Indirect": diffuse only, affectSpecular 0) light HDRP's reference
renders about 0.4 as much as the same light does in URP, outdoors and in facility rooms alike (the sun matches); the
cause is not known, the factor is measured (FILL_LIGHT_FACTOR).
HDRP lights with a large shape radius (the player's night-vision light: 2.2 m) are soft spheres whose illuminance near
them goes as I / (d^2 + r^2); URP's are points (I / d^2), so they are dimmed by d^2 / (d^2 + r^2) at d = 2 m (matched against reference renders of the ship).

Prefab instances that override a light's m_Intensity are rescaled too (the target is checked to be a Light).

Cameras: HDRP clears from HDAdditionalCameraData (clearColorMode Sky/Color/None, backgroundColorHDR) and ignores the
Camera's own clear flags; URP uses the Camera's. Sky becomes black for now (URP would draw the scene's unused skybox
material: the launch screen came out white), Color the HDRP background colour, None a depth-only clear.

Usage: <venv>/bin/python scripts/convert_scenes.py <unity project> [--ev100 1.3]
"""

import argparse
import math
import re
import sys
from pathlib import Path

EXPOSURE_EV100 = 1.3
DOC_RE = re.compile(r"^--- !u!(\d+) &(-?\d+)[^\n]*\n", re.M)
LIGHT_CLASS = "108"
CAMERA_CLASS = "20"
CLEAR_MODE_RE = re.compile(r"^  clearColorMode: (\d+)$", re.M)
BACKGROUND_HDR_RE = re.compile(r"^  backgroundColorHDR: (\{[^}]*\})$", re.M)
CLEAR_FLAGS_RE = re.compile(r"^(  m_ClearFlags: )\d+$", re.M)
BACKGROUND_RE = re.compile(r"^(  m_BackGroundColor: )\{[^}]*\}$", re.M)
MONO_CLASS = "114"
PREFAB_INSTANCE_CLASS = "1001"
GUID_RE = re.compile(r"^guid: ([0-9a-f]{32})", re.M)
GAMEOBJECT_RE = re.compile(r"^  m_GameObject: \{fileID: (-?\d+)\}", re.M)
INTENSITY_RE = re.compile(r"^(  m_Intensity: )([-0-9.eE+]+)$", re.M)
DIMMER_RE = re.compile(r"^  m_LightDimmer: ([-0-9.eE+]+)$", re.M)
RADIUS_RE = re.compile(r"^  m_ShapeRadius: ([-0-9.eE+]+)$", re.M)
SOFT_RADIUS = 0.5
FILL_LIGHT_FACTOR = 0.4
AFFECT_SPECULAR_RE = re.compile(r"^  m_AffectSpecular: (\d)$", re.M)
LIGHT_TYPE_RE = re.compile(r"^  m_Type: (\d)$", re.M)
SOFT_DISTANCE = 2.0
MOD_RE = re.compile(r"(    - target: \{fileID: (-?\d+), guid: ([0-9a-f]{32}), type: 3\}\n"
                    r"      propertyPath: m_Intensity\n"
                    r"      value: )([-0-9.eE+]+)")
SOURCE_PREFAB_RE = re.compile(r"m_SourcePrefab: \{fileID: \d+, guid: ([0-9a-f]{32}), type: 3\}")


def docs(text: str):
    """(class id, file id, start, end) of every YAML document"""
    marks = list(DOC_RE.finditer(text))
    for i, m in enumerate(marks):
        yield m.group(1), m.group(2), m.start(), marks[i + 1].start() if i + 1 < len(marks) else len(text)


def fmt(value: float) -> str:
    return f"{value:.6g}"


class LightIndex:
    """Which file ids of a prefab are Light components (prefab instance overrides name their target by file id)"""

    def __init__(self, assets: Path):
        self.paths = {}
        for meta in assets.rglob("*.prefab.meta"):
            m = GUID_RE.search(meta.read_text(errors="ignore"))
            if m:
                self.paths[m.group(1)] = meta.with_suffix("")
        self.cache: dict[str, set[str]] = {}

    def lights(self, guid: str) -> set[str]:
        if guid not in self.cache:
            self.cache[guid] = set()
            path = self.paths.get(guid)
            if path and path.exists():
                text = path.read_text(errors="surrogateescape")
                found = {fid for cls, fid, _, _ in docs(text) if cls == LIGHT_CLASS}
                # Lights of prefabs nested in this one are addressed through their stripped placeholders
                for cls, fid, start, end in docs(text):
                    if cls == LIGHT_CLASS and "stripped" in text[start:text.index("\n", start)]:
                        found.add(fid)
                self.cache[guid] = found
        return self.cache[guid]


def convert(path: Path, scale: float, index: LightIndex) -> int:
    text = path.read_text(errors="surrogateescape")
    if "m_Intensity" not in text and "clearColorMode" not in text:
        return 0
    dimmers, clears, diffuse_only = {}, {}, set()
    for cls, fid, start, end in docs(text):
        if cls == MONO_CLASS:
            body = text[start:end]
            d, go = DIMMER_RE.search(body), GAMEOBJECT_RE.search(body)
            if d and go:
                radius = RADIUS_RE.search(body)
                r = float(radius.group(1)) if radius else 0.0
                soft = SOFT_DISTANCE ** 2 / (SOFT_DISTANCE ** 2 + r * r) if r > SOFT_RADIUS else 1.0
                spec = AFFECT_SPECULAR_RE.search(body)
                dimmers[go.group(1)] = float(d.group(1)) * soft
                if spec and spec.group(1) == "0":
                    diffuse_only.add(go.group(1))
            mode, color = CLEAR_MODE_RE.search(body), BACKGROUND_HDR_RE.search(body)
            if mode and color and go:
                clears[go.group(1)] = (int(mode.group(1)), color.group(1))

    pieces, last, count = [], 0, 0
    for cls, fid, start, end in docs(text):
        if cls == LIGHT_CLASS and "stripped" not in text[start:text.index("\n", start)]:
            body = text[start:end]
            go = GAMEOBJECT_RE.search(body)
            factor = scale * dimmers.get(go.group(1) if go else "", 1.0)
            light_type = LIGHT_TYPE_RE.search(body)
            if go and go.group(1) in diffuse_only and light_type and light_type.group(1) == "1":
                factor *= FILL_LIGHT_FACTOR
            body, n = INTENSITY_RE.subn(lambda m: m.group(1) + fmt(float(m.group(2)) * factor), body, count=1)
            pieces += [text[last:start], body]
            last, count = end, count + n
        elif cls == CAMERA_CLASS and "stripped" not in text[start:text.index("\n", start)]:
            body = text[start:end]
            go = GAMEOBJECT_RE.search(body)
            clear = clears.get(go.group(1) if go else "")
            if clear:
                mode, color = clear
                flags = {0: 2, 1: 2, 2: 3}.get(mode, 2)  # Sky, Color -> SolidColor; None -> Depth
                background = color if mode == 1 else "{r: 0, g: 0, b: 0, a: 0}"
                body = CLEAR_FLAGS_RE.sub(lambda m: m.group(1) + str(flags), body, count=1)
                body = BACKGROUND_RE.sub(lambda m: m.group(1) + background, body, count=1)
                count += 1
            pieces += [text[last:start], body]
            last = end
        elif cls == PREFAB_INSTANCE_CLASS:
            body = text[start:end]
            source = SOURCE_PREFAB_RE.search(body)
            lights = index.lights(source.group(1)) if source else set()

            def mod(m: re.Match) -> str:
                nonlocal count
                if m.group(3) == source.group(1) and m.group(2) in lights:
                    count += 1
                    return m.group(1) + fmt(float(m.group(4)) * scale)
                return m.group(0)

            pieces += [text[last:start], MOD_RE.sub(mod, body)]
            last = end
    pieces.append(text[last:])
    if count:
        path.write_text("".join(pieces), errors="surrogateescape", newline="\n")
    return count


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("project", type=Path)
    parser.add_argument("--ev100", type=float, default=EXPOSURE_EV100)
    args = parser.parse_args()

    scale = 1 / (1.2 * 2 ** args.ev100) / math.pi
    assets = args.project / "Assets"
    index = LightIndex(assets)
    files = lights = 0
    for path in sorted(assets.rglob("*")):
        if path.suffix in (".unity", ".prefab") and path.is_file():
            n = convert(path, scale, index)
            files += n > 0
            lights += n
    print(f"lights rescaled by {scale:.4f} (EV100 {args.ev100}), camera clears from HDRP: {lights} values in {files} files")
    return 0


if __name__ == "__main__":
    sys.exit(main())
