#!/usr/bin/env bash
# What the headset shows (the compositor's frame, one eye), through the system screenshot service: Unity's own capture
# (quest-diag.sh) only sees the 2D window, which is black while the game renders to the headset.
#   scripts/quest-shot.sh [<name>]   -> work/diag/vr/<name or time>.jpg
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
adb="/Applications/Unity/Hub/Editor/2022.3.62f3/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb"
dir=/sdcard/Oculus/Screenshots

before="$("$adb" shell ls -t "$dir" 2>/dev/null | head -1 || true)"
"$adb" shell am startservice -n com.oculus.metacam/.capture.CaptureService -a TAKE_SCREENSHOT >/dev/null
for _ in $(seq 1 20); do
    sleep 0.5
    latest="$("$adb" shell ls -t "$dir" 2>/dev/null | head -1 || true)"
    if [ -n "$latest" ] && [ "$latest" != "$before" ]; then
        sleep 1  # the service writes the file after creating it
        mkdir -p "$root/work/diag/vr"
        out="$root/work/diag/vr/${1:-$(date +%Y%m%d-%H%M%S)}.jpg"
        "$adb" pull "$dir/$latest" "$out" >/dev/null
        "$adb" shell rm "$dir/$latest"
        echo "$out"
        exit 0
    fi
done
echo "no screenshot after 10 s" >&2
exit 1
