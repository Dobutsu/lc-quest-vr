"""Write the URP project's versions of LCVR's own shaders over their placeholders (in Assets/Shader, by shader name).

LCVR's TextMeshPro variants are TextMeshPro's own shaders with another name, so they are made from the TMP shaders the
project has (Assets/TextMesh Pro/Shaders, from TMP's essential resources; package_swap.py puts them there):
  - "TextMeshPro/Distance Field (Always On Top)" (LCVR's world-space HUD text): TMP_SDF with depth testing off
  - "Mixed Reality Toolkit/TextMeshPro" (the terminal keyboard's labels): TMP_SDF-Mobile
LCVR's vignette post-process shader is left a placeholder: on URP, HdrpBridge draws the vignette volume with URP's own.
Usage: python scripts/lcvr_shaders.py <unity project>
"""

import re
import sys
from pathlib import Path

SHADER_NAME_RE = re.compile(r'^(\ufeff?\s*Shader\s+")([^"]+)(")', re.M)

# shader name -> (TMP source, edits)
VARIANTS = {
    "TextMeshPro/Distance Field (Always On Top)": ("TMP_SDF.shader", [("ZTest [unity_GUIZTestMode]", "ZTest Always")]),
    "Mixed Reality Toolkit/TextMeshPro": ("TMP_SDF-Mobile.shader", []),
}


def main() -> int:
    project = Path(sys.argv[1])
    tmp = project / "Assets" / "TextMesh Pro" / "Shaders"
    written = []
    for placeholder in sorted((project / "Assets" / "Shader").glob("*.shader")):
        match = SHADER_NAME_RE.search(placeholder.read_text(errors="ignore"))
        if not match or match.group(2) not in VARIANTS:
            continue
        name = match.group(2)
        source, edits = VARIANTS[name]
        text = (tmp / source).read_text()
        text = SHADER_NAME_RE.sub(lambda m: f"{m.group(1)}{name}{m.group(3)}", text, count=1)
        # The includes sit next to the TMP shaders, not next to this one
        text = re.sub(r'#include "(TMPro[^"]*\.cginc)"', r'#include "Assets/TextMesh Pro/Shaders/\1"', text)
        text = re.sub(r'^Fallback "[^"]*"\s*$', "", text, flags=re.M)
        for old, new in edits:
            if old not in text:
                print(f"{source}: '{old}' not found", file=sys.stderr)
                return 1
            text = text.replace(old, new)
        placeholder.write_text(text, newline="\n")
        written.append(name)
    print(f"LCVR shaders written: {', '.join(written) or 'none'}")
    return 0 if len(written) == len(VARIANTS) else 1


if __name__ == "__main__":
    sys.exit(main())
