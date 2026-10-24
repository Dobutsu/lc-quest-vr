#!/usr/bin/env bash
# Bake LCVR's Harmony patches into the game assemblies.
#   scripts/bake.sh [universal|all] [windows|android]   -> work/baked/<target>-<platform>/ + bake-report.json
# Inputs:  work/game (APFS clone of the Lethal Company install), external/LCVR + patches/lcvr (scripts/prepare-lcvr.sh),
#          work/downloads/LCVR-<ver> (release zip: RuntimeDeps)
# android: platform overrides from tools/LCVR.Standalone ([Replaces]), BepInEx's 0Harmony rewritten next to the game's;
#          Input System is a package compiled from source on Quest, so its patch (InputSystem.actions) is hooked at call sites
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
target="${1:-all}"
platform="${2:-windows}"
dotnet="${DOTNET:-$HOME/.dotnet/dotnet}"
release="$root/work/downloads/LCVR-1.5.0/BepInEx/plugins/LCVR"

"$root/scripts/prepare-lcvr.sh" >/dev/null
"$dotnet" build -c Release "$root/tools/HarmonyBaker" -v quiet -nologo >/dev/null

extra=()
if [ "$platform" = android ]; then
    "$dotnet" build -c Release "$root/tools/LCVR.Standalone" -v quiet -nologo >/dev/null
    extra=(--overrides "$root/tools/LCVR.Standalone/bin/Release/netstandard2.1/LCVR.Standalone.dll"
           --rewrite "$root/work/downloads/BepInEx5/BepInEx/core/0Harmony.dll"
           --callsite-hooks Unity.InputSystem)
fi

out="$root/work/baked/$target-$platform"
rm -rf "$out"
"$dotnet" "$root/tools/HarmonyBaker/bin/Release/net8.0/HarmonyBaker.dll" \
    --game "$root/work/game/Lethal Company_Data/Managed" \
    --mod "$root/work/lcvr-src/bin/Release/netstandard2.1/LCVR.dll" \
    --deps "$release/RuntimeDeps" \
    --deps "$HOME/.nuget/packages/bepinex.baselib" \
    --platform "$platform" \
    --targets "$target" \
    --out "$out" ${extra[@]+"${extra[@]}"}
