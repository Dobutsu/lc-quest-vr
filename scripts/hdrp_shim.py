"""Replace the HDRP runtime DLL of the Unity project with a shim of the same name (tools/ShimMaker).

HDRP does not run on Android, but the game's code and LCVR call into it, and scenes, prefabs and volume profiles hold
HDRP components (lights, cameras, decals, local fog, volume overrides, custom passes). The shim keeps every class the
assets reference, with its serialized fields, and every member the code uses; method bodies that would reach HDRP's
renderer become stubs. The .meta (and so every script reference) stays as it is. URP-side behaviour (light units,
decals, fog) is done by our runtime code reading the shim components.

Usage: <venv>/bin/python scripts/hdrp_shim.py <unity project> [--scan <dll> ...] [--report <file>]
"""

import argparse
import re
import sys
from pathlib import Path

import dnfile

sys.path.insert(0, str(Path(__file__).parent / "lib"))
from paths import GAME, WORK, run_tool  # noqa: E402
from unity_fileid import script_file_id  # noqa: E402

HDRP = "Unity.RenderPipelines.HighDefinition.Runtime"
# Assemblies the shim must not reference: they leave the project
DROP = ["Unity.RenderPipelines.HighDefinition.Config.Runtime", "Unity.VisualEffectGraph.Runtime"]
# Bodies that touch these are stubbed even when they could be kept: the renderer and its per-frame systems
STUB_TYPES = [
    "UnityEngine.Rendering.HighDefinition.HDRenderPipeline",
    "UnityEngine.Rendering.HighDefinition.HDCamera",
    "UnityEngine.Rendering.HighDefinition.DecalSystem",
    "UnityEngine.Rendering.HighDefinition.LightLoop",
    "UnityEngine.Rendering.HighDefinition.HDShadowManager",
    "UnityEngine.Rendering.HighDefinition.LocalVolumetricFogManager",
    "UnityEngine.Rendering.HighDefinition.HDLightRenderDatabase",
]

# Attributes that would make the shim's types misbehave under URP: the pipeline filters name HDRenderPipeline, which
# is no render pipeline here (RP Core throws from their constructors on every volume component lookup)
DROP_ATTRIBUTES = [
    "UnityEngine.Rendering.VolumeComponentMenuForRenderPipeline",
    "UnityEngine.Rendering.SupportedOnRenderPipelineAttribute",
]

SCRIPT_REF = re.compile(r"fileID: (-?\d+), guid: ([0-9a-f]{32}), type: 3")
MANAGED_REF = re.compile(r"type: \{class: (\w+), ns: ([\w.]+), asm: " + re.escape(HDRP) + r"\}")
YAML_SUFFIXES = {".unity", ".prefab", ".asset", ".mat", ".controller", ".playable", ".overrideController"}


def type_names(dll: Path) -> dict[int, str]:
    md = dnfile.dnPE(data=dll.read_bytes()).net.mdtables
    text = lambda v: str(v.value) if hasattr(v, "value") else str(v)
    return {script_file_id(text(r.TypeNamespace), text(r.TypeName)): f"{text(r.TypeNamespace)}.{text(r.TypeName)}".lstrip(".")
            for r in md.TypeDef.rows}


def references(dll: Path, assembly: str) -> bool:
    try:
        refs = dnfile.dnPE(data=dll.read_bytes()).net.mdtables.AssemblyRef
    except Exception:  # noqa: BLE001 - native or unreadable DLL
        return False
    return any(str(r.Name) == assembly for r in (refs.rows if refs else []))


def asset_roots(assets: Path, dll: Path, guid: str) -> set[str]:
    names = type_names(dll)
    roots = set()
    for path in assets.rglob("*"):
        if path.suffix not in YAML_SUFFIXES or not path.is_file():
            continue
        text = path.read_text(errors="ignore")
        if guid in text:
            for m in SCRIPT_REF.finditer(text):
                if m.group(2) == guid and int(m.group(1)) in names:
                    roots.add(names[int(m.group(1))])
        for m in MANAGED_REF.finditer(text):
            roots.add(f"{m.group(2)}.{m.group(1)}")
    return roots


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("project", type=Path)
    parser.add_argument("--scan", type=Path, nargs="*", action="extend", default=[])
    parser.add_argument("--search", type=Path, nargs="*", action="extend", default=[], help="more folders to resolve scanned DLLs' references")
    parser.add_argument("--assets", type=Path, help="where to look for references (default: <project>/Assets)")
    parser.add_argument("--report", type=Path, default=WORK / "logs" / "hdrp-shim.txt")
    args = parser.parse_args()

    plugins = args.project / "Assets" / "Plugins"
    dll = plugins / f"{HDRP}.dll"
    original = WORK / "hdrp-original" / f"{HDRP}.dll"
    if not original.exists():
        original.parent.mkdir(parents=True, exist_ok=True)
        original.write_bytes(dll.read_bytes())

    guid = re.search(r"guid: (\w+)", Path(str(dll) + ".meta").read_text()).group(1)
    roots = sorted(asset_roots(args.assets or args.project / "Assets", original, guid))
    roots_file = WORK / "hdrp-original" / "roots.txt"
    roots_file.write_text("\n".join(roots) + "\n", newline="\n")
    print(f"HDRP classes referenced by assets: {len(roots)}")

    managed = GAME / "Lethal Company_Data" / "Managed"
    # Every DLL of the project that calls into HDRP (the game, DunGen, ProBuilder's HDRP support...)
    scan = [d for d in sorted(plugins.glob("*.dll")) if d.name != dll.name and references(d, HDRP)] + args.scan
    print("scanned for HDRP use: " + ", ".join(d.stem for d in scan))
    tool_args = ["--input", str(original), "--out", str(dll), "--search", str(managed), "--roots", str(roots_file),
                 "--report", str(args.report)]
    for s in scan:
        tool_args += ["--scan", str(s)]
    for d in args.search + sorted({s.parent for s in args.scan}):
        tool_args += ["--search", str(d)]
    for d in DROP:
        tool_args += ["--drop", d]
    for a in DROP_ATTRIBUTES:
        tool_args += ["--drop-attribute", a]
    for t in STUB_TYPES:
        tool_args += ["--stub-type", t]
    args.report.parent.mkdir(parents=True, exist_ok=True)
    run_tool("shim-maker", tool_args)
    return 0


if __name__ == "__main__":
    sys.exit(main())
