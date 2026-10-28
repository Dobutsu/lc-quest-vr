"""Which version of each Unity package the game was built with.

The export keeps packages as precompiled DLLs without version strings. For each package, every version in the Unity
registry allowed for Unity 2022.3 is compared with the game's DLLs: type names first, then member names (methods,
fields, properties) declared in the package's runtime sources. The best match (Jaccard similarity) wins; ties are
reported so they can be checked by hand. Netcode and Transport must match the PC game exactly.

Usage: <venv>/bin/python scripts/package_versions.py <export>/Assets/Plugins [--candidates 15] [--only name ...]
"""

import argparse
import io
import json
import re
import ssl
import sys
import tarfile
import urllib.request
from pathlib import Path

import certifi
import dnfile

sys.path.insert(0, str(Path(__file__).parent / "lib"))
from paths import WORK  # noqa: E402

CACHE = WORK / "packages"
SSL = ssl.create_default_context(cafile=certifi.where())

# package -> game DLLs it compiles to
PACKAGES = {
    "com.unity.netcode.gameobjects": ["Unity.Netcode.Runtime", "Unity.Netcode.Components"],
    "com.unity.transport": ["Unity.Networking.Transport"],
    "com.unity.burst": ["Unity.Burst"],
    "com.unity.collections": ["Unity.Collections"],
    "com.unity.mathematics": ["Unity.Mathematics"],
    "com.unity.inputsystem": ["Unity.InputSystem"],
    "com.unity.textmeshpro": ["Unity.TextMeshPro"],
    "com.unity.ai.navigation": ["Unity.AI.Navigation"],
    "com.unity.animation.rigging": ["Unity.Animation.Rigging"],
    "com.unity.probuilder": ["Unity.ProBuilder"],
    "com.unity.timeline": ["Unity.Timeline"],
    "com.unity.render-pipelines.core": ["Unity.RenderPipelines.Core.Runtime"],
    "com.unity.render-pipelines.high-definition": ["Unity.RenderPipelines.HighDefinition.Runtime"],
    "com.unity.visualeffectgraph": ["Unity.VisualEffectGraph.Runtime"],
    "com.unity.multiplayer.tools": ["Unity.Multiplayer.Tools.NetStats"],
    "com.unity.services.core": ["Unity.Services.Core"],
    "com.unity.services.relay": ["Unity.Services.Relay"],
    "com.unity.xr.management": ["Unity.XR.Management"],
    "com.unity.xr.openxr": ["Unity.XR.OpenXR"],
    "com.unity.xr.core-utils": ["Unity.XR.CoreUtils"],
    "com.unity.xr.legacyinputhelpers": ["UnityEngine.XR.LegacyInputHelpers"],
    "com.unity.profiling.core": ["Unity.Profiling.Core"],
}

TYPE_DECL = re.compile(r"\b(?:class|struct|interface|enum|record)\s+([A-Za-z_]\w*)")
DELEGATE_DECL = re.compile(r"\bdelegate\s+[\w<>\[\],.? ]+?\s+([A-Za-z_]\w*)\s*[<(]")
VERSION_RE = re.compile(r"^(\d+)\.(\d+)\.(\d+)(?:-(.+))?$")


def registry(name: str) -> dict:
    cached = CACHE / f"{name}.registry.json"
    if not cached.exists():
        CACHE.mkdir(parents=True, exist_ok=True)
        cached.write_bytes(urllib.request.urlopen(f"https://packages.unity.com/{name}", context=SSL).read())
    return json.loads(cached.read_text())


def fetch(name: str, version: str, meta: dict) -> Path:
    dest = CACHE / f"{name}@{version}"
    if not dest.is_dir():
        data = urllib.request.urlopen(meta["versions"][version]["dist"]["tarball"], context=SSL).read()
        with tarfile.open(fileobj=io.BytesIO(data)) as tar:
            tar.extractall(dest, filter="data")
    return dest / "package"


def unity_ok(info: dict) -> bool:
    unity = info.get("unity")
    if not unity:
        return True
    major, minor = (int(x) for x in unity.split(".")[:2])
    return (major, minor) <= (2022, 3)


def sort_key(version: str):
    m = VERSION_RE.match(version)
    return (int(m[1]), int(m[2]), int(m[3]), m[4] is None, m[4] or "") if m else (0, 0, 0, False, version)


def dll_types(path: Path) -> set[str]:
    pe = dnfile.dnPE(data=path.read_bytes())
    names = set()
    for row in pe.net.mdtables.TypeDef.rows:
        name = str(row.TypeName.value if hasattr(row.TypeName, "value") else row.TypeName).split("`")[0]
        if name != "<Module>" and not name.startswith("<") and "$" not in name and "__" not in name:
            names.add(name)
    return names


MEMBER_DECL = re.compile(
    r"^\s*(?:\[[^\]]*\]\s*)*(?:(?:public|internal|private|protected|static|readonly|override|virtual|abstract|"
    r"sealed|extern|unsafe|new|const|volatile|partial|async)\s+)+[\w<>\[\],.?* ]+?\s+(\w+)\s*(?:[({=;]|=>)", re.M)


def dll_members(path: Path) -> set[str]:
    md = dnfile.dnPE(data=path.read_bytes()).net.mdtables
    text = lambda v: str(v.value) if hasattr(v, "value") else str(v)
    names = set()
    for table in (md.MethodDef, md.Field, md.Property):
        for row in (table.rows if table else []):
            name = text(row.Name)
            if not name.startswith(("<", ".", "get_", "set_", "add_", "remove_", "op_")) and "$" not in name and "__" not in name:
                names.add(name)
    return names


def source_members(package: Path) -> set[str]:
    names = set()
    for cs in package.rglob("*.cs"):
        parts = {p.lower() for p in cs.relative_to(package).parts}
        if parts & {"editor", "tests", "samples~", "tests~", "documentation~", "samples"}:
            continue
        names.update(MEMBER_DECL.findall(cs.read_text(errors="ignore")))
    return names


def source_types(package: Path) -> set[str]:
    names = set()
    for cs in package.rglob("*.cs"):
        parts = {p.lower() for p in cs.relative_to(package).parts}
        if parts & {"editor", "tests", "samples~", "tests~", "documentation~", "samples"}:
            continue
        text = cs.read_text(errors="ignore")
        text = re.sub(r"//.*", "", text)
        names.update(TYPE_DECL.findall(text))
        names.update(DELEGATE_DECL.findall(text))
    return names


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("plugins", type=Path)
    parser.add_argument("--candidates", type=int, default=0, help="only the latest N versions (0 = all)")
    parser.add_argument("--only", nargs="*")
    args = parser.parse_args()

    result = {}
    for name, dlls in PACKAGES.items():
        if args.only and name not in args.only:
            continue
        game, game_members = set(), set()
        for dll in dlls:
            path = args.plugins / f"{dll}.dll"
            if path.exists():
                game |= dll_types(path)
                game_members |= dll_members(path)
        if not game:
            print(f"{name}: no DLL in the export, skipped")
            continue

        meta = registry(name)
        versions = sorted((v for v, info in meta["versions"].items() if unity_ok(info) and "-" not in v), key=sort_key)
        scores = []
        for version in versions[-args.candidates:] if args.candidates else versions:
            try:
                package = fetch(name, version, meta)
                src, members = source_types(package), source_members(package)
            except Exception as exc:  # noqa: BLE001 - a broken tarball must not stop the rest
                print(f"  {name}@{version}: {exc}", file=sys.stderr)
                continue
            jaccard = len(game & src) / len(game | src) if src else 0
            member_jaccard = len(game_members & members) / len(game_members | members) if members else 0
            scores.append((round(jaccard, 3), round(member_jaccard, 4), version))
        scores.sort(reverse=True)
        best = scores[0]
        close = [s[2] for s in scores[1:] if s[0] == best[0] and s[1] == best[1]]
        print(f"{name}: {best[2]}  (types {best[0]}, members {best[1]})"
              + (f"  TIE with {', '.join(close)}" if close else "")
              + f"  next: {', '.join(f'{v} {t}/{m}' for t, m, v in scores[1:4])}")
        result[name] = {"version": best[2], "ties": close, "scores": scores[:6]}

    (WORK / "inventory").mkdir(parents=True, exist_ok=True)
    (WORK / "inventory" / "package-versions.json").write_text(json.dumps(result, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
