"""Inventory of a Lethal Company install: what has to change to run it on Android/Quest (HDRP -> URP above all).

Usage: <venv>/bin/python scripts/inventory.py work/game [--json out.json]
Reads only; never writes into the game directory.
"""

import argparse
import collections
import json
import re
import sys
from pathlib import Path

import UnityPy

DATA_DIR = "Lethal Company_Data"

# Shaders that ship with the engine or with packages whose source we can recompile (or replace with URP's)
BUILTIN_PREFIXES = (
    "Standard", "Hidden/", "Legacy Shaders/", "Mobile/", "Nature/", "Particles/", "Skybox/",
    "Sprites/", "UI/", "Unlit/", "GUI/", "TextMeshPro/", "Autodesk Interactive", "HDRP/",
    "Renderers/", "Shader Graphs/",
)

# MonoBehaviours from HDRP (and core volume components) that carry rendering data the URP port has to translate
HDRP_SCRIPTS = (
    "HDAdditionalLightData", "HDAdditionalCameraData", "HDAdditionalReflectionData", "PlanarReflectionProbe",
    "DecalProjector", "LocalVolumetricFog", "CustomPassVolume", "Volume", "VolumeProfile", "WaterSurface",
)


def unity_version(data_dir: Path) -> str:
    head = (data_dir / "globalgamemanagers").read_bytes()[:128]
    match = re.search(rb"\d{4}\.\d+\.\d+[abfp]\d+", head)
    return match.group(0).decode() if match else "unknown"


def script_name(obj, cache: dict) -> str:
    """Class name of a MonoBehaviour's MonoScript (cached by path id: thousands of behaviours share a few scripts).
    Release builds carry no type trees for MonoBehaviours, so only the base fields (m_Script) can be read"""
    data = obj.read(check_read=False)
    ptr = data.m_Script
    key = (ptr.m_FileID, ptr.m_PathID, obj.assets_file.name)
    if key not in cache:
        try:
            script = ptr.read()
            cache[key] = script.m_ClassName
        except Exception:  # noqa: BLE001 - unresolved script: keep counting the rest
            cache[key] = "?"
    return cache[key], data


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("game_dir", type=Path)
    parser.add_argument("--json", type=Path)
    args = parser.parse_args()

    data_dir = args.game_dir / DATA_DIR
    report: dict = {
        "unity_version": unity_version(data_dir),
        "native_plugins": sorted(p.name for p in (data_dir / "Plugins" / "x86_64").glob("*")),
        "managed_assemblies": sorted(p.name for p in (data_dir / "Managed").glob("*.dll")),
        "scenes": sorted((p.name for p in data_dir.glob("level*") if p.suffix == ""), key=lambda n: int(n[5:])),
    }

    env = UnityPy.load(str(data_dir))
    type_counts: collections.Counter = collections.Counter()
    tex_formats: collections.Counter = collections.Counter()
    tex_bytes = 0
    shaders: dict[int, str] = {}
    material_shaders: collections.Counter = collections.Counter()
    light_types: collections.Counter = collections.Counter()
    shadow_lights = 0
    scripts: collections.Counter = collections.Counter()
    script_cache: dict = {}
    camera_targets = 0
    volume_components: collections.Counter = collections.Counter()

    # Shaders first, so materials can name theirs
    for obj in env.objects:
        if obj.type.name == "Shader":
            try:
                shader = obj.read()
                name = shader.m_ParsedForm.m_Name if hasattr(shader, "m_ParsedForm") else shader.m_Name
                shaders[(obj.assets_file.name, obj.path_id)] = name
            except Exception as exc:  # noqa: BLE001
                print(f"warn: shader {obj.path_id}: {exc}", file=sys.stderr)

    for obj in env.objects:
        type_name = obj.type.name
        type_counts[type_name] += 1
        try:
            if type_name == "Texture2D":
                tex = obj.read()
                fmt = getattr(tex.m_TextureFormat, "name", str(tex.m_TextureFormat))
                tex_formats[fmt] += 1
                tex_bytes += tex.m_CompleteImageSize
            elif type_name == "Material":
                mat = obj.read()
                try:
                    shader = mat.m_Shader.read()
                    name = shader.m_ParsedForm.m_Name if hasattr(shader, "m_ParsedForm") else shader.m_Name
                except Exception:  # noqa: BLE001 - missing/stripped shader
                    name = "<missing>"
                material_shaders[name] += 1
            elif type_name == "Light":
                light = obj.read()
                kind = getattr(light.m_Type, "name", str(light.m_Type))
                light_types[{0: "Spot", 1: "Directional", 2: "Point", 3: "Area"}.get(light.m_Type, kind)] += 1
                if light.m_Shadows.m_Type != 0:
                    shadow_lights += 1
            elif type_name == "Camera":
                cam = obj.read()
                if cam.m_TargetTexture.m_PathID != 0:
                    camera_targets += 1
            elif type_name == "MonoBehaviour":
                name, data = script_name(obj, script_cache)
                scripts[name] += 1
                if name == "VolumeProfile":
                    for comp in getattr(data, "components", []) or []:
                        try:
                            volume_components[script_name(comp.deref(), script_cache)[0]] += 1
                        except Exception:  # noqa: BLE001
                            volume_components["?"] += 1
        except Exception as exc:  # noqa: BLE001 - inventory must keep going
            type_counts[f"!read_error:{type_name}"] += 1
            print(f"warn: {type_name} {obj.path_id}: {exc}", file=sys.stderr)

    names = sorted(set(shaders.values()))
    custom = [s for s in names if not s.startswith(BUILTIN_PREFIXES)]
    report.update({
        "object_counts": dict(type_counts.most_common()),
        "texture_formats": dict(tex_formats.most_common()),
        "texture_mbytes": round(tex_bytes / 2**20, 1),
        "shaders_total": len(names),
        "shaders_custom": custom,
        "shaders_builtin_like": [s for s in names if s not in custom],
        "materials_by_shader": dict(material_shaders.most_common()),
        "lights": {"by_type": dict(light_types.most_common()), "with_shadows": shadow_lights},
        "cameras_with_target_texture": camera_targets,
        "hdrp_components": {k: v for k, v in scripts.most_common() if k in HDRP_SCRIPTS},
        "volume_components": dict(volume_components.most_common()),
        "scripts_top": dict(scripts.most_common(60)),
    })

    text = json.dumps(report, indent=2, ensure_ascii=False)
    if args.json:
        args.json.write_text(text)
    print(text)
    return 0


if __name__ == "__main__":
    sys.exit(main())
