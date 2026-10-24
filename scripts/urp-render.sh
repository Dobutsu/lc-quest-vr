#!/usr/bin/env bash
# The same views rendered by the URP project in the editor, for A/B images against scripts/reference-render.sh.
#   scripts/urp-render.sh <views file> [<out dir>]   -> work/urp-out/<name>.png
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
out="${2:-$root/work/urp-out}"
mkdir -p "$out" "$root/work/logs"
out="$(cd "$out" && pwd)"
"/Applications/Unity/Hub/Editor/2022.3.62f3/Unity.app/Contents/MacOS/Unity" -batchmode -timestamps \
    -projectPath "$root/work/project" -buildTarget Android \
    -executeMethod LCQuest.Editor.ReferenceRender.Run ${NEUTRAL:+-neutral} ${COMPOSITE:+-composite} ${TUNE:+-tune "$TUNE"} -views "$(cd "$(dirname "$1")" && pwd)/$(basename "$1")" -out "$out" \
    -logFile "$root/work/logs/urp-render.log"
grep "\[ReferenceRender\]" "$root/work/logs/urp-render.log" | cut -d'|' -f3- || true
