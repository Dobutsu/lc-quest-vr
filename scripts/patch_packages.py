"""Embed the packages LCVR patches into the VR project's Packages/ and apply those patches as source edits.

HarmonyBaker can only bake patches into assemblies the project ships precompiled. Packages compiled from source lose
a baked body, so their patches are either hooked at the game's call sites (bake.sh --callsite-hooks, Input System) or
applied here. The Quest build always runs in VR, so the edits are unconditional, like LCVR's VR patch set:
  - UIPatches.ForceNewInputSystem: XRUIInputModule navigates with Input System actions
Patches left out on purpose: XRPatches.OnBeforeOpenXRInitialize (XR Management starts nothing by itself on Quest,
LCVR initializes XR and sets its features), XRPatches.OnXRSystemInitialize and MirrorViewOffsetPatch (HDRP's occlusion
mesh colour and desktop mirror view).
Usage: python scripts/patch_packages.py <unity project>
"""

import json
import shutil
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from package_swap import fetch_package  # noqa: E402

MARKER = "LCVR (Quest)"

# package, file inside it, anchor, replacement
PATCHES = [
    ("com.unity.xr.interaction.toolkit", "Runtime/UI/XRUIInputModule.cs",
     """        void ProcessNavigation()
        {
""",
     f"""        void ProcessNavigation()
        {{
            // {MARKER}: UIPatches.ForceNewInputSystem
            if (activeInputMode != ActiveInputMode.InputSystemActions)
                activeInputMode = ActiveInputMode.InputSystemActions;

"""),
]


def main() -> int:
    project = Path(sys.argv[1])
    manifest = json.loads((project / "Packages" / "manifest.json").read_text())["dependencies"]

    for name in sorted({p[0] for p in PATCHES}):
        target = project / "Packages" / name
        if not target.is_dir():
            # Without its tests: an embedded package compiles them, and they need the Test Framework
            shutil.copytree(fetch_package(name, manifest[name]), target, ignore=shutil.ignore_patterns("Tests", "Tests.meta"))

    for name, relative, anchor, replacement in PATCHES:
        path = project / "Packages" / name / relative
        text = path.read_text()
        if replacement in text:
            print(f"already patched: {name}/{relative}")
            continue
        if text.count(anchor) != 1:
            print(f"anchor not found once in {name}/{relative}", file=sys.stderr)
            return 1
        path.write_text(text.replace(anchor, replacement), newline="\n")
        print(f"patched: {name}/{relative}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
