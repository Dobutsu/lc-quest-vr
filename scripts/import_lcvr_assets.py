"""Bring LCVR's asset bundles into the Quest project as source assets, so the editor rebuilds them for Android.

LCVR's bundles (lethalcompanyvr, lethalcompanyvr-levels) are built for Windows and HDRP: D3D11 shaders, BC textures.
Its Unity project is not public, so scripts/export.sh exports them (with the game's DLLs, LCVR.dll and XRI's DLL, for
the scripts) into work/lcvr-export, and this copies what the bundles contain into Assets/LCVR/:
  - the bundles' container assets and everything they reference, followed by GUID;
  - assets the project already has (same GUID: AssetRipper derives them deterministically) are not copied;
  - shaders are matched by name to the project's (rewritten for URP or swapped for package shaders later); the others
    join the placeholders in Assets/Shader as LCVR_*.shader (scripts/lcvr_shaders.py);
  - LCVR.dll's .meta (the baked LCVR.dll goes next to it) and XRI's DLL (package_swap.py --vr replaces it by the package).
Assets/LCVR/bundles.json lists each bundle's assets for unity-tools/EditorXR/LcvrBundles.cs.
Run before package_swap.py, which retargets the copied assets' package script references with the rest.
Usage: <venv>/bin/python scripts/import_lcvr_assets.py <lcvr export>/ExportedProject <unity project> <bundle>... [--dry-run]
"""

import json
import re
import shutil
import sys
from collections import deque
from pathlib import Path

import UnityPy

GUID_RE = re.compile(r"guid: ([0-9a-f]{32})")
SHADER_NAME_RE = re.compile(r'^\ufeff?\s*Shader\s+"([^"]+)"', re.M)
TARGET = "LCVR"


def meta_guid(path: Path) -> str | None:
    meta = Path(str(path) + ".meta")
    if not meta.exists():
        return None
    match = GUID_RE.search(meta.read_text(errors="ignore"))
    return match.group(1) if match else None


def is_yaml(path: Path) -> bool:
    with path.open("rb") as f:
        return f.read(5) == b"%YAML"


def guid_index(assets: Path) -> dict[str, Path]:
    index = {}
    for meta in assets.rglob("*.meta"):
        target = meta.with_suffix("")
        if target.is_file():
            guid = meta_guid(target)
            if guid:
                index[guid] = target
    return index


def shader_names(folder: Path) -> dict[str, str]:
    names = {}
    for shader in folder.rglob("*.shader") if folder.is_dir() else []:
        match = SHADER_NAME_RE.search(shader.read_text(errors="ignore"))
        guid = meta_guid(shader)
        if match and guid:
            names.setdefault(match.group(1), guid)
    return names


def containers(bundle: Path) -> list[str]:
    env = UnityPy.load(str(bundle))
    for obj in env.objects:
        if obj.type.name == "AssetBundle":
            return sorted({path for path, _ in obj.read().m_Container})
    return []


def main() -> int:
    export, project = Path(sys.argv[1]), Path(sys.argv[2])
    dry_run = "--dry-run" in sys.argv
    bundles = [Path(p) for p in sys.argv[3:] if p != "--dry-run"]
    source_assets = export / "Assets"
    assets = project / "Assets"
    target_root = assets / TARGET

    source_index = guid_index(source_assets)
    project_index = guid_index(assets)
    project_guids = set(project_index)
    plugin_guids = {g for g, p in source_index.items() if p.parent == source_assets / "Plugins"}

    # Shaders: the bundles' copies are swapped for the project's shader of the same name
    project_shaders = shader_names(assets / "Shader")
    shader_map = {}
    for guid, path in source_index.items():
        if path.suffix == ".shader":
            match = SHADER_NAME_RE.search(path.read_text(errors="ignore"))
            if match and match.group(1) in project_shaders:
                shader_map[guid] = project_shaders[match.group(1)]

    # Container paths -> exported files (case-insensitive, extension-insensitive: .inputactions became .asset)
    by_key = {}
    for path in source_assets.rglob("*"):
        if path.is_file() and path.suffix != ".meta":
            rel = path.relative_to(source_assets).as_posix().lower()
            by_key.setdefault(rel, path)
            by_key.setdefault(rel.rsplit(".", 1)[0], path)
    manifest = {}
    roots = []
    missing = []
    for bundle in bundles:
        entries = []
        for container in containers(bundle):
            key = container.lower().removeprefix("assets/")
            path = by_key.get(key) or by_key.get(key.rsplit(".", 1)[0])
            if path is None:
                missing.append(f"{bundle.name}: {container}")
                continue
            roots.append(path)
            # A shader the project has stands in for the bundle's copy and comes along as a dependency; LCVR's own
            # shaders stay listed (it loads them by name: AssetManager.TMPAlwaysOnTop, VignettePostProcess)
            if path.suffix != ".shader" or meta_guid(path) not in shader_map:
                entries.append(path)
        manifest[bundle.name] = entries

    # Everything the roots reference, inside the export
    copy = {}
    queue = deque(roots)
    while queue:
        path = queue.popleft()
        guid = meta_guid(path)
        if path in copy.values() or guid in plugin_guids or guid in shader_map:
            continue
        if guid in project_guids:
            continue
        copy[guid or str(path)] = path
        if is_yaml(path):
            for ref in GUID_RE.findall(path.read_text(errors="surrogateescape")):
                if ref in source_index and ref not in copy:
                    queue.append(source_index[ref])

    def destination(path: Path) -> Path:
        # Shaders join the export's placeholders: package_swap.py swaps those a package provides, lcvr_shaders.py
        # writes the rest
        # LCVR loads some by file name from its bundle (AssetManager: "TextMeshPro Always On Top", "Vignette"), so the
        # name stays unless the project has one like it
        if path.suffix == ".shader":
            plain = assets / "Shader" / path.name
            return plain if not plain.exists() else assets / "Shader" / f"LCVR_{path.name}"
        return target_root / path.relative_to(source_assets)

    if dry_run:
        for path in sorted(copy.values()):
            print(f"  copy {path.relative_to(source_assets)}")
        print(f"{len(copy)} to copy, {len(shader_map)} shaders matched; containers missing: {missing}")
        return 0
    if target_root.exists():
        shutil.rmtree(target_root)
    # Decided before anything is written (the existence check above must not see our own copies)
    destinations = {path: destination(path) for path in copy.values()}
    rewritten = 0
    for path in copy.values():
        dest = destinations[path]
        dest.parent.mkdir(parents=True, exist_ok=True)
        if is_yaml(path) and shader_map:
            text = path.read_text(errors="surrogateescape")
            updated = GUID_RE.sub(lambda m: f"guid: {shader_map.get(m.group(1), m.group(1))}", text)
            rewritten += updated != text
            dest.write_text(updated, errors="surrogateescape", newline="\n")
        else:
            shutil.copy2(path, dest)
        shutil.copy2(Path(str(path) + ".meta"), Path(str(dest) + ".meta"))

    # LCVR's DLL keeps the export's GUID (scripts point at it); the baked DLL is copied in later. XRI's DLL is swapped.
    plugins = assets / "Plugins"
    (plugins / TARGET).mkdir(parents=True, exist_ok=True)
    shutil.copy2(source_assets / "Plugins" / "LCVR.dll.meta", plugins / TARGET / "LCVR.dll.meta")
    for name in ("Unity.XR.Interaction.Toolkit.dll", "Unity.XR.Interaction.Toolkit.dll.meta"):
        shutil.copy2(source_assets / "Plugins" / name, plugins / name)

    def project_path(path: Path) -> str:
        guid = meta_guid(path)
        if guid in project_guids:  # the project's own copy of the same asset
            return project_index[guid].relative_to(project).as_posix()
        return destinations[path].relative_to(project).as_posix()

    bundles_json = {name: [project_path(p) for p in entries] for name, entries in manifest.items()}
    (target_root / "bundles.json").write_text(json.dumps(bundles_json, indent=2) + "\n", newline="\n")

    print(f"LCVR assets: {len(copy)} copied to Assets/{TARGET}, {len(shader_map)} shaders matched to the project's, "
          f"{rewritten} files retargeted; bundles: " + ", ".join(f"{n} {len(e)} assets" for n, e in manifest.items()))
    for item in missing:
        print(f"  container asset not in the export: {item}")
    return 1 if missing else 0


if __name__ == "__main__":
    sys.exit(main())
