#!/usr/bin/env bash
# Reference (HDRP) against ours (URP) for every view of a views file: one line per view, ref/ours ratios of a few
# regions (1.0 = same brightness; >1 = ours darker).
#   scripts/compare-set.sh <views file> [neutral|full|composite]
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
python="${PYTHON:-$root/work/tools/venv/bin/python}"  # uv venv with builder/python/requirements.lock
mode="${2:-neutral}"
case "$mode" in
    neutral) ref="$root/work/reference-out/neutral"; ours="$root/work/urp-out/neutral"; regions=(l:220,100,430,400 r:430,100,860,400 t:220,0,860,100 b:220,400,860,520) ;;
    full) ref="$root/work/reference-out"; ours="$root/work/urp-out"; regions=(l:220,100,430,400 r:430,100,860,400 t:220,0,860,100 b:220,400,860,520) ;;
    composite) ref="$root/work/reference-out/composite"; ours="$root/work/urp-out/composite"; regions=(l:300,150,640,600 r:640,150,1000,600 t:300,40,1000,200 b:300,600,1000,780) ;;
esac
cut -d'|' -f1 "$1" | sed 's/ *$//' | grep -v '^#' | while read -r view; do
    [ -f "$ref/$view.png" ] && [ -f "$ours/$view.png" ] || { echo "$view: missing"; continue; }
    printf '%-28s %s\n' "$view" "$("$python" -W ignore "$root/scripts/compare_images.py" "$ref/$view.png" "$ours/$view.png" --brief "${regions[@]}")"
done
