#!/usr/bin/env bash
# Export LCVR's asset bundles with AssetRipper, for scripts/import_lcvr_assets.py. AssetRipper reads MonoBehaviours
# with the scripts' assemblies, so it gets a minimal game folder: the game's managers and Managed DLLs (APFS clones),
# plus LCVR.dll, XRI's DLL and the two bundles.
#   scripts/export-lcvr.sh   -> work/lcvr-export/ExportedProject (+ work/logs/assetripper-lcvr.log)
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
game="$root/work/game"
release="$root/work/downloads/LCVR-1.5.0/BepInEx/plugins/LCVR"
input="$root/work/lcvr-input"
data="$input/Lethal Company_Data"

rm -rf "$input"
mkdir -p "$data/StreamingAssets/LCVR"
cp -c "$game/Lethal Company.exe" "$input/"
for file in app.info boot.config globalgamemanagers globalgamemanagers.assets globalgamemanagers.assets.resS; do
    cp -c "$game/Lethal Company_Data/$file" "$data/"
done
cp -cR "$game/Lethal Company_Data/Resources" "$data/Resources"
cp -cR "$game/Lethal Company_Data/Managed" "$data/Managed"
cp "$release/LCVR.dll" "$release/RuntimeDeps/Unity.XR.Interaction.Toolkit.dll" "$data/Managed/"
cp "$release/lethalcompanyvr" "$release/lethalcompanyvr-levels" "$data/StreamingAssets/LCVR/"

"$root/scripts/export.sh" "$input" "$root/work/lcvr-export" assetripper-lcvr
