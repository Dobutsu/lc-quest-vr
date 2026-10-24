#!/usr/bin/env bash
# The Unity project for the Quest build, recreated from the AssetRipper export (scripts/export.sh) every time; its
# Library (Unity's import cache, the slow part) is kept.
#   scripts/make-project.sh [--flat]   -> work/project
# Steps: package DLLs -> packages (URP in place of HDRP), HDRP shim, game assemblies renamed, lights and camera clears for URP,
# libopus for Dissonance, our editor and runtime scripts. The VR build (default; --flat: the plain game, stage 1) also gets
# the game assemblies with LCVR's patches baked in (scripts/bake.sh all android), LCVR and its runtime, LCVR's bundle
# assets (rebuilt for Android by the editor, unity-tools/EditorXR/LcvrBundles.cs) and the XR packages.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
work="$root/work"
export_dir="$work/export/ExportedProject"
project="$work/project"
python="${PYTHON:-$root/work/tools/venv/bin/python}"  # uv venv with builder/python/requirements.lock
unity_version="2022.3.62f3"
unity_changeset="96770f904ca7"
release="$work/downloads/LCVR-1.5.0/BepInEx/plugins/LCVR"
lcvr="$release/LCVR.dll"
bepinex="$work/downloads/BepInEx5/BepInEx/core"
baked="$work/baked/all-android"
vr=1
[ "${1:-}" = --flat ] && vr=0

[ -d "$export_dir/Assets" ] || { echo "no export at $export_dir (scripts/export.sh)" >&2; exit 1; }
[ -f "$work/opus-android/libopus.so" ] || "$root/scripts/build-opus-android.sh"
[ -f "$work/dissonance-android/libAudioPluginDissonance.so" ] || "$root/scripts/build-dissonance-android.sh"
if [ "$vr" = 1 ]; then
    # LCVR (with patches/lcvr) baked into the game; the bake is quick, so it always runs
    "$root/scripts/bake.sh" all android >/dev/null || { echo "bake failed: see $baked/bake-report.json" >&2; exit 1; }
    lcvr="$baked/LCVR.dll"
    [ -f "$work/lcvr-export/ExportedProject/Assets/Plugins/LCVR.dll.meta" ] || "$root/scripts/export-lcvr.sh"
fi

step() { echo "== $*"; }

step "copy the export (keeping Library)"
if [ -d "$project/Library" ]; then
    rm -rf "$work/Library.keep"
    mv "$project/Library" "$work/Library.keep"
fi
rm -rf "$project"
cp -cR "$export_dir" "$project"
[ -d "$work/Library.keep" ] && mv "$work/Library.keep" "$project/Library"

printf 'm_EditorVersion: %s\nm_EditorVersionWithRevision: %s (%s)\n' "$unity_version" "$unity_version" "$unity_changeset" \
    > "$project/ProjectSettings/ProjectVersion.txt"
# Parallel import: textures and models import in worker processes
sed -i '' 's/^  m_RefreshImportMode: [0-9]*$/  m_RefreshImportMode: 1/' "$project/ProjectSettings/EditorSettings.asset"

# HDRP's global settings would pull its runtime resources (compute shaders, ~200) into the build
sed -i '' '/^    UnityEngine.Rendering.HighDefinition.HDRenderPipeline: /d' "$project/ProjectSettings/GraphicsSettings.asset"

# Fog variants: "Automatic" strips them from every shader since no scene enables fog; HdrpBridge fogs at runtime
sed -i '' 's/^  m_FogStripping: [0-9]*$/  m_FogStripping: 1/' "$project/ProjectSettings/GraphicsSettings.asset"

android_meta() { sed "s/{GUID}/$(printf '%s' "$2" | md5 -q)/" "$root/unity-tools/Plugins/android-managed.meta.template" > "$1.meta"; }
if [ "$vr" = 1 ]; then
    step "baked game assemblies"
    # Same file names, so the export's .meta (and every reference to the DLL) stays
    for dll in Assembly-CSharp DissonanceVoip Facepunch.Steamworks.Win64 Unity.Netcode.Runtime Unity.ProBuilder; do
        cp "$baked/$dll.dll" "$project/Assets/Plugins/$dll.dll"
    done
    step "LCVR and its runtime"
    "$python" "$root/scripts/import_lcvr_assets.py" "$work/lcvr-export/ExportedProject" "$project" \
        "$release/lethalcompanyvr" "$release/lethalcompanyvr-levels"
    lcvr_plugins="$project/Assets/Plugins/LCVR"
    cp "$baked/LCVR.dll" "$lcvr_plugins/LCVR.dll"  # .meta from the bundle export: LCVR's scripts keep their GUID
    # Runtime only (Android): the bootstrap, BepInEx for the plugin base class, logging and config, and HarmonyX
    # (with the IL2CPP-safe invoker baked in) for LCVR's AccessTools calls
    "${DOTNET_ROOT:-$HOME/.dotnet}/dotnet" build "$root/tools/LCVR.Standalone" -c Release -v quiet -nologo >/dev/null
    cp "$root/tools/LCVR.Standalone/bin/Release/netstandard2.1/LCVR.Standalone.dll" "$baked/0Harmony.dll" "$lcvr_plugins/"
    for name in BepInEx MonoMod.Utils MonoMod.RuntimeDetour Mono.Cecil; do
        cp "$bepinex/$name.dll" "$lcvr_plugins/"
    done
    for dll in LCVR.Standalone 0Harmony BepInEx MonoMod.Utils MonoMod.RuntimeDetour Mono.Cecil; do
        android_meta "$lcvr_plugins/$dll.dll" "lcquest-lcvr:$dll.dll"
    done
fi

step "package DLLs -> packages"
swap_args=()
[ "$vr" = 1 ] && swap_args=(--vr)
"$python" "$root/scripts/package_swap.py" "$project" ${swap_args[@]+"${swap_args[@]}"}
if [ "$vr" = 1 ]; then
    "$python" "$root/scripts/lcvr_shaders.py" "$project"
    "$python" "$root/scripts/patch_packages.py" "$project"
fi
step "HDRP shim"
# Our runtime code is scanned too (compiled outside Unity against the original HDRP); it needs the package assemblies
# of an earlier import, so a first build goes without it
runtime_scan=()
if [ -d "$project/Library/ScriptAssemblies" ] && [ -f "$work/hdrp-original/Unity.RenderPipelines.HighDefinition.Runtime.dll" ]; then
    "${DOTNET_ROOT:-$HOME/.dotnet}/dotnet" build "$root/tools/UnityScripts" -c Release -v quiet -nologo >/dev/null
    runtime_scan=(--scan "$root/tools/UnityScripts/bin/Release/netstandard2.1/LCQuest.Runtime.dll")
fi
"$python" "$root/scripts/hdrp_shim.py" "$project" --scan "$lcvr" ${runtime_scan[@]+"${runtime_scan[@]}"} --search "$bepinex"
step "rename game assemblies"
rename_extra=()
[ "$vr" = 1 ] && rename_extra=("$project/Assets/Plugins/LCVR/LCVR.dll")
"$python" "$root/scripts/rename_game_assemblies.py" "$project" ${rename_extra[@]+"${rename_extra[@]}"}
step "lights and camera clears for URP"
"$python" "$root/scripts/convert_scenes.py" "$project"

step "texture import settings"
"$python" "$root/scripts/fix_textures.py" "$project"

step "libopus and AudioPluginDissonance for Dissonance"
native="$project/Assets/Plugins/Android/arm64-v8a"
mkdir -p "$native"
for lib in "$work/opus-android/libopus.so" "$work/dissonance-android/libAudioPluginDissonance.so"; do
    name="$(basename "$lib" .so)"
    cp "$lib" "$native/$name.so"
    sed "s/{GUID}/$(printf "lcquest-native:$name" | md5 -q)/" "$root/unity-tools/Plugins/android-arm64.meta.template" \
        > "$native/$name.so.meta"
done

step "shaders rewritten for URP"
# Same file names (and so .meta GUIDs) and shader names as AssetRipper's placeholders: the game's materials bind as is
for shader in "$root/unity-tools/Shaders/"*.shader; do
    target="$project/Assets/Shader/$(basename "$shader")"
    [ -f "$target" ] || { echo "no placeholder $(basename "$shader") in the export" >&2; exit 1; }
    cp "$shader" "$target"
done

step "editor and runtime scripts"
mkdir -p "$project/Assets/LCQuest/Editor" "$project/Assets/LCQuest/Runtime"
cp "$root/unity-tools/Editor/"*.cs "$root/unity-tools/Reference/"*.cs "$project/Assets/LCQuest/Editor/"
if [ "$vr" = 1 ]; then
    cp "$root/unity-tools/EditorXR/"*.cs "$project/Assets/LCQuest/Editor/"
    cp "$root/unity-tools/RuntimeXR/link.xml" "$project/Assets/LCQuest/link.xml"
fi
cp "$root/unity-tools/Runtime/"*.cs "$project/Assets/LCQuest/Runtime/"
# Our own shaders (Shader.Find or our materials use them)
mkdir -p "$project/Assets/LCQuest/Shaders"
cp "$root/unity-tools/Runtime/Shaders/"*.shader "$project/Assets/LCQuest/Shaders/"
# Soft Mask's shaders (the export's are placeholders) over theirs in Resources, where the DLL loads them by name
cp "$root/unity-tools/SoftMask/"*.shader "$project/Assets/Resources/"
cp "$root/unity-tools/SoftMask/"*.hlsl "$root/unity-tools/SoftMask/"*.cginc "$project/Assets/LCQuest/Shaders/"
echo "project ready: $project"
