"""Turn the precompiled Unity package DLLs of an AssetRipper export back into real Unity packages.

Why: the export keeps every package (Input System, uGUI, TMP, RP Core, VFX Graph...) as a DLL in Assets/Plugins.
URP needs the real RP Core, Shader Graph, Burst and Mathematics packages (duplicate assemblies otherwise), the Input
System DLL was compiled for Windows (no Android devices), and package shaders were exported as dummies. So we swap each DLL for the package of the same version and retarget every asset reference:
  - scripts:  {fileID: <hash(ns+class)>, guid: <dll guid>, type: 3} -> {fileID: 11500000, guid: <.cs guid>, type: 3}
  - shaders:  guid of the exported dummy shader -> guid of the package shader with the same name
Usage: <venv>/bin/python scripts/package_swap.py <unity project> [--dry-run] [--pipeline urp|hdrp] [--vr]
"""

import argparse
import io
import json
import re
import ssl
import sys
import tarfile
import urllib.request
from collections import Counter, defaultdict
from pathlib import Path

import certifi
import dnfile

sys.path.insert(0, str(Path(__file__).parent / "lib"))
from paths import WORK, builtin_packages  # noqa: E402
from unity_fileid import script_file_id  # noqa: E402

CACHE = WORK / "packages"
BUILTIN_PACKAGES = builtin_packages()

# package, version, DLLs in Assets/Plugins it replaces (versions from scripts/package_versions.py; the render
# pipeline packages are the ones built into Unity 2022.3.62, as in the game)
PACKAGES = [
    ("com.unity.ugui", "1.0.0", ["UnityEngine.UI"]),
    ("com.unity.textmeshpro", "3.0.9", ["Unity.TextMeshPro"]),
    ("com.unity.inputsystem", "1.8.1", ["Unity.InputSystem", "Unity.InputSystem.ForUI"]),
    ("com.unity.mathematics", "1.2.6", ["Unity.Mathematics"]),
    ("com.unity.burst", "1.8.21", ["Unity.Burst", "Unity.Burst.Unsafe"]),
    ("com.unity.render-pipelines.core", "14.0.12", ["Unity.RenderPipelines.Core.Runtime", "Unity.RenderPipelines.Core.ShaderLibrary"]),
    ("com.unity.shadergraph", "14.0.12", ["Unity.RenderPipelines.ShaderGraph.ShaderGraphLibrary"]),
    ("com.unity.visualeffectgraph", "14.0.12", ["Unity.VisualEffectGraph.Runtime"]),
    ("com.unity.render-pipelines.universal", "14.0.12", []),
]

# --pipeline hdrp: the reference project (scripts/make-reference.sh) keeps HDRP, as the real package, to render
# what the PC game shows
HDRP_PACKAGES = [
    ("com.unity.render-pipelines.high-definition", "14.0.12", ["Unity.RenderPipelines.HighDefinition.Runtime"]),
    ("com.unity.render-pipelines.high-definition-config", "14.0.12", ["Unity.RenderPipelines.HighDefinition.Config.Runtime"]),
]

# --vr: LCVR's XR stack. XR Interaction Toolkit replaces the DLL LCVR ships (its asset bundle's scripts point at it,
# scripts/import_lcvr_assets.py brings it in); OpenXR and XR Management are added as packages only: the game's DLLs are
# Windows builds, and only the game's XR settings assets use them (removed below, QuestXRSetup creates new ones).
# Versions: the XR stack the R.E.P.O. port runs on the Quest (OpenXR 1.14.3 has the Meta Quest feature), XRI as in
# LCVR's bundle (its starter assets sample is 2.5.4)
VR_PACKAGES = [
    ("com.unity.xr.interaction.toolkit", "2.5.4", ["Unity.XR.Interaction.Toolkit"]),
    ("com.unity.xr.management", "4.5.0", []),
    ("com.unity.xr.openxr", "1.14.3", []),
]

# DLLs that leave the project without a package in their place, with the ScriptableObject assets of their classes:
# HDRP's shader config (the HDRP shim does not need it) and XR (Windows builds; the VR build adds the XR packages)
REMOVE = [
    "Unity.RenderPipelines.HighDefinition.Config.Runtime",
    "Unity.XR.Management", "Unity.XR.OpenXR", "Unity.XR.OpenXR.Features.ConformanceAutomation",
    "Unity.XR.OpenXR.Features.MetaQuestSupport", "Unity.XR.OpenXR.Features.MockRuntime",
    "Unity.XR.OpenXR.Features.OculusQuestSupport", "Unity.XR.OpenXR.Features.RuntimeDebugger",
    "Unity.XR.CoreUtils", "UnityEngine.SpatialTracking", "UnityEngine.XR.LegacyInputHelpers",
]
M_SCRIPT_RE = re.compile(r"^  m_Script: \{fileID: -?\d+, guid: ([0-9a-f]{32}), type: 3\}", re.M)

# python.org builds ship without a CA store
SSL = ssl.create_default_context(cafile=certifi.where())

GUID_RE = re.compile(r"guid: ([0-9a-f]{32})")
SCRIPT_REF_RE = re.compile(r"fileID: (-?\d+), guid: ([0-9a-f]{32}), type: 3")
COMPUTE_REF_RE = re.compile(r"fileID: 7200000, guid: ([0-9a-f]{32}), type: [23]")
SHADER_NAME_RE = re.compile(r'^\ufeff?\s*Shader\s+"([^"]+)"', re.M)


def meta_guid(path: Path) -> str:
    return GUID_RE.search(Path(str(path) + ".meta").read_text()).group(1)


def fetch_package(name: str, version: str) -> Path:
    builtin = BUILTIN_PACKAGES / name
    if builtin.is_dir() and json.loads((builtin / "package.json").read_text())["version"] == version:
        return builtin

    dest = CACHE / f"{name}@{version}"
    if not dest.is_dir():
        meta = json.load(urllib.request.urlopen(f"https://packages.unity.com/{name}", context=SSL))
        tarball = meta["versions"][version]["dist"]["tarball"]
        data = urllib.request.urlopen(tarball, context=SSL).read()
        with tarfile.open(fileobj=io.BytesIO(data)) as tar:
            tar.extractall(dest, filter="data")
    return dest / "package"


def unitypackage_shaders(package: Path, assets: Path, write: bool) -> dict[str, str]:
    """TMP 3.x ships its shaders inside "TMP Essential Resources.unitypackage", not as package files.
    Extract only the shader folder (keeping the GUIDs Unity's importer would use) and return name -> guid."""
    shaders: dict[str, str] = {}
    for bundle in package.rglob("TMP Essential Resources.unitypackage"):
        with tarfile.open(bundle, "r:gz") as tar:
            entries: dict[str, dict[str, bytes]] = defaultdict(dict)
            for member in tar.getmembers():
                if member.isfile():
                    guid, _, part = member.name.lstrip("./").partition("/")
                    entries[guid][part] = tar.extractfile(member).read()
        for guid, parts in entries.items():
            pathname = parts.get("pathname", b"").decode().splitlines()[0] if "pathname" in parts else ""
            if not pathname.startswith("Assets/TextMesh Pro/Shaders/") or "asset" not in parts:
                continue
            if write:
                target = assets.parent / pathname
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(parts["asset"])
                Path(str(target) + ".meta").write_bytes(parts["asset.meta"])
            match = SHADER_NAME_RE.search(parts["asset"].decode(errors="ignore"))
            if pathname.endswith(".shader") and match:
                shaders[match.group(1)] = guid
    return shaders


def dll_types(path: Path) -> list[tuple[str, str]]:
    pe = dnfile.dnPE(data=path.read_bytes())
    md = pe.net.mdtables
    nested = {row.NestedClass.row_index for row in (md.NestedClass.rows if md.NestedClass else [])}
    text = lambda v: str(v.value) if hasattr(v, "value") else str(v)
    return [(text(t.TypeNamespace), text(t.TypeName)) for i, t in enumerate(md.TypeDef.rows, start=1)
            if i not in nested and text(t.TypeName) != "<Module>"]


def find_script(package: Path, namespace: str, name: str) -> Path | None:
    candidates = [p for p in sorted(package.rglob(f"{name}.cs")) if Path(str(p) + ".meta").exists()]
    if len(candidates) <= 1:
        return candidates[0] if candidates else None
    ns_decl = re.compile(rf"namespace\s+{re.escape(namespace)}\b") if namespace else None
    matching = [p for p in candidates if (ns_decl.search(p.read_text(errors="ignore")) if ns_decl else "namespace" not in p.read_text(errors="ignore"))]
    return matching[0] if matching else None


def is_yaml(path: Path) -> bool:
    try:
        with path.open("rb") as f:
            return f.read(5) == b"%YAML"
    except OSError:
        return False


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("project", type=Path)
    parser.add_argument("--dry-run", action="store_true")
    parser.add_argument("--pipeline", choices=["urp", "hdrp"], default="urp")
    parser.add_argument("--vr", action="store_true")
    args = parser.parse_args()
    global PACKAGES, REMOVE
    if args.pipeline == "hdrp":
        PACKAGES = [p for p in PACKAGES if p[0] != "com.unity.render-pipelines.universal"] + HDRP_PACKAGES
        REMOVE = [d for d in REMOVE if d != "Unity.RenderPipelines.HighDefinition.Config.Runtime"]
    if args.vr:
        PACKAGES = PACKAGES + VR_PACKAGES

    assets = args.project / "Assets"
    plugins = assets / "Plugins"

    script_map: dict[tuple[str, int], str] = {}
    class_names: dict[tuple[str, int], str] = {}
    dll_guids: dict[str, str] = {}
    package_shaders: dict[str, str] = {}
    package_computes: dict[str, str] = {}
    manifest_adds: dict[str, str] = {}

    for package_name, version, dlls in PACKAGES:
        package = fetch_package(package_name, version)
        manifest_adds[package_name] = version

        for shader in sorted(package.rglob("*.shader")):
            match = SHADER_NAME_RE.search(shader.read_text(errors="ignore"))
            if match and Path(str(shader) + ".meta").exists():
                package_shaders[match.group(1)] = meta_guid(shader)
        # Shader Graphs too (HDRP's default local fog material uses one: without it the reference project drew no
        # local volumetric fog): their shader name is the graph's path plus its file name
        for graph in sorted(package.rglob("*.shadergraph")):
            if not Path(str(graph) + ".meta").exists():
                continue
            match = re.search(r'"m_Path": "([^"]*)"', graph.read_text(errors="ignore"))
            path = match.group(1) if match and match.group(1) else "Shader Graphs"
            package_shaders.setdefault(f"{path}/{graph.stem}", meta_guid(graph))
        package_shaders.update(unitypackage_shaders(package, assets, write=not args.dry_run))
        for compute in sorted(package.rglob("*.compute")):
            if Path(str(compute) + ".meta").exists():
                package_computes[compute.stem] = meta_guid(compute)

        for dll in dlls:
            path = plugins / f"{dll}.dll"
            if not path.exists():
                print(f"note: {dll}.dll not in Plugins (already swapped?)")
                continue
            guid = meta_guid(path)
            dll_guids[guid] = dll
            for namespace, name in dll_types(path):
                script = find_script(package, namespace, name)
                if script:
                    key = (guid, script_file_id(namespace, name))
                    script_map[key] = meta_guid(script)
                    class_names[key] = f"{namespace}.{name}".lstrip(".")

    # Exported dummy shaders that the packages provide for real
    shader_map: dict[str, str] = {}
    removed_files: list[Path] = []
    for dummy in (assets / "Shader").glob("*.shader"):
        match = SHADER_NAME_RE.search(dummy.read_text(errors="ignore"))
        if match and match.group(1) in package_shaders:
            shader_map[meta_guid(dummy)] = package_shaders[match.group(1)]
            removed_files.append(dummy)
    # Compute shaders come out of AssetRipper as serialized ComputeShader .assets (DX11 bytecode inside), referenced
    # as {fileID: 7200000, guid: X, type: 2}; an imported .compute is the same fileID with type 3
    compute_map: dict[str, str] = {}
    compute_dir = assets / "ComputeShader"
    for exported in list(compute_dir.glob("*.asset")) + list(compute_dir.glob("*.compute")) if compute_dir.is_dir() else []:
        if exported.stem in package_computes:
            compute_map[meta_guid(exported)] = package_computes[exported.stem]
            removed_files.append(exported)

    # Rewrite references
    unmapped: Counter = Counter()
    touched = 0
    replaced = Counter()
    for path in list(assets.rglob("*")) + list((args.project / "ProjectSettings").glob("*.asset")):
        if not path.is_file() or path.suffix == ".meta" or not is_yaml(path):
            continue
        original = path.read_text(errors="surrogateescape")

        def script_sub(m: re.Match) -> str:
            file_id, guid = int(m.group(1)), m.group(2)
            if guid not in dll_guids:
                return m.group(0)
            key = (guid, file_id)
            if key in script_map:
                replaced["script"] += 1
                return f"fileID: 11500000, guid: {script_map[key]}, type: 3"
            unmapped[(dll_guids[guid], file_id)] += 1
            return m.group(0)

        updated = SCRIPT_REF_RE.sub(script_sub, original)
        if compute_map:
            updated = COMPUTE_REF_RE.sub(lambda m: (replaced.__setitem__("compute", replaced["compute"] + 1) or
                                                    f"fileID: 7200000, guid: {compute_map[m.group(1)]}, type: 3")
                                         if m.group(1) in compute_map else m.group(0), updated)
        if shader_map:
            updated = GUID_RE.sub(lambda m: (replaced.__setitem__("shader", replaced["shader"] + 1) or f"guid: {shader_map[m.group(1)]}")
                                  if m.group(1) in shader_map else m.group(0), updated)
        if updated != original:
            touched += 1
            if not args.dry_run:
                path.write_text(updated, errors="surrogateescape", newline="\n")

    print(f"script classes mapped: {len(script_map)}, shaders mapped: {len(shader_map)}, compute shaders mapped: {len(compute_map)}")
    print(f"files rewritten: {touched}, script refs: {replaced['script']}, shader refs: {replaced['shader']}, "
          f"compute refs: {replaced['compute']}")
    if unmapped:
        print("UNMAPPED script references (class not found in package):")
        for (dll, file_id), count in unmapped.most_common():
            print(f"  {dll} fileID {file_id}: {count} refs")

    if args.dry_run:
        return 0

    for guid, dll in dll_guids.items():
        removed_files.append(plugins / f"{dll}.dll")
    removed_guids = {meta_guid(plugins / f"{dll}.dll"): dll for dll in REMOVE if (plugins / f"{dll}.dll").exists()}
    for dll in removed_guids.values():
        removed_files.append(plugins / f"{dll}.dll")
    for asset in assets.rglob("*.asset"):
        if asset.is_file() and is_yaml(asset):
            scripts = set(M_SCRIPT_RE.findall(asset.read_text(errors="ignore")))
            if scripts and scripts <= set(removed_guids):
                removed_files.append(asset)
    for path in removed_files:
        path.unlink(missing_ok=True)
        Path(str(path) + ".meta").unlink(missing_ok=True)

    manifest_path = args.project / "Packages" / "manifest.json"
    manifest = json.loads(manifest_path.read_text())
    manifest["dependencies"].update(manifest_adds)
    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", newline="\n")
    print(f"removed {len(removed_files)} files, added {len(manifest_adds)} packages to manifest")
    return 0


if __name__ == "__main__":
    sys.exit(main())
