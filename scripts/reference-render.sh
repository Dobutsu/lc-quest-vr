#!/usr/bin/env bash
# Reference images from the HDRP project (scripts/make-reference.sh) for the views in a file (format:
# unity-tools/Reference/ReferenceRender.cs; poses come from scripts/quest-diag.sh's "pose" lines).
#   scripts/reference-render.sh <views file> [<out dir>]   -> work/reference-out/<name>.png
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
out="${2:-$root/work/reference-out}"
mkdir -p "$out" "$root/work/logs"
out="$(cd "$out" && pwd)"
"/Applications/Unity/Hub/Editor/2022.3.62f3/Unity.app/Contents/MacOS/Unity" -batchmode -timestamps \
    -projectPath "$root/work/reference" -buildTarget StandaloneOSX \
    -executeMethod LCQuest.Editor.ReferenceRender.Run ${NEUTRAL:+-neutral} ${COMPOSITE:+-composite} -views "$(cd "$(dirname "$1")" && pwd)/$(basename "$1")" -out "$out" \
    -logFile "$root/work/logs/reference-render.log"
grep "\[ReferenceRender\]" "$root/work/logs/reference-render.log" | cut -d'|' -f3- || true
