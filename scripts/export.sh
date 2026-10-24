#!/usr/bin/env bash
# Export the game into a Unity project with AssetRipper (free GUI build, driven headless through its local web API),
# with the same settings as the REPO port's builder (ExportStep.cs).
#   scripts/export.sh [<input folder> <output folder> <log name>]
#     default: work/game -> work/export/ExportedProject (+ work/logs/assetripper.log)
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
ripper="$root/work/tools/assetripper-2.0.0/AssetRipper.GUI.Free"
game="${1:-$root/work/game}"
out="${2:-$root/work/export}"
log="$root/work/logs/${3:-assetripper}.log"
port=47321

mkdir -p "$root/work/logs"
rm -rf "$out" "$log"
"$ripper" --headless --port "$port" --log --log-path "$log" > "${log%.log}.out" 2>&1 &
pid=$!
trap 'kill $pid 2>/dev/null || true' EXIT

until grep -q "Now listening on: http" "${log%.log}.out" 2>/dev/null; do
    kill -0 $pid 2>/dev/null || { echo "AssetRipper exited" >&2; exit 1; }
    sleep 1
done

post() { curl -fsS -o /dev/null -w "%{http_code} $1\n" -X POST "http://127.0.0.1:$port$1" "${@:2}"; }
post /Settings/Update \
    -d ScriptContentLevel=Level2 -d StreamingAssetsMode=Extract -d BundledAssetsExportMode=DirectExport \
    -d AudioExportFormat=Default -d ImageExportFormat=Png -d LightmapTextureExportFormat=Yaml \
    -d ScriptExportMode=DllExportWithoutRenaming -d ScriptLanguageVersion=AutoSafe -d ShaderExportMode=Decompile \
    -d SpriteExportMode=Yaml -d TextExportMode=Parse
post /LoadFolder --data-urlencode "Path=$game"
post /Export/UnityProject --data-urlencode "Path=$out"

[ -f "$out/ExportedProject/ProjectSettings/ProjectSettings.asset" ] || { echo "no project exported, see $log" >&2; exit 1; }
echo "exported: $out/ExportedProject ($(du -sh "$out/ExportedProject" | cut -f1))"
