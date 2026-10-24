#!/usr/bin/env bash
# Snapshot of the running game on the Quest (unity-tools/Runtime/QuestDiagnostics.cs), or a click on a menu button.
#   scripts/quest-diag.sh              -> work/diag/<time>/{screen.png, rt-*.png, report.txt}
#   scripts/quest-diag.sh click <text> -> clicks the active button whose name, path or label contains <text>
#   scripts/quest-diag.sh invoke <Type.Method> -> calls a parameterless method on the first object of that type
#   scripts/quest-diag.sh tp <x y z [yaw] | ship | outside | entrance [yaw]> -> teleports the local player
#   scripts/quest-diag.sh hide <text> -> switches off (or back on) the renderers whose path contains <text>
# The game must be running and not paused (headset on, or: adb shell am broadcast -a com.oculus.vrpowermanager.prox_close).
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
adb="/Applications/Unity/Hub/Editor/2022.3.62f3/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb"
files="/sdcard/Android/data/io.lcvr.quest/files"

case "${1:-}" in
    click)
        # Waits (up to 35 s) until the game has clicked it or given up: the next step may need its result
        shift; "$adb" shell "echo '$*' > $files/ui-click"
        for _ in $(seq 1 70); do "$adb" shell ls "$files/ui-click" >/dev/null 2>&1 || exit 0; sleep 0.5; done
        echo "click '$*' still pending" >&2; exit 1 ;;
    invoke)
        # One file the game polls every 0.5 s: wait until it has taken it, so a following invoke does not replace it
        shift; "$adb" shell "echo '$*' > $files/invoke"
        for _ in $(seq 1 10); do "$adb" shell ls "$files/invoke" >/dev/null 2>&1 || exit 0; sleep 0.3; done
        exit 0 ;;
    tp) shift; "$adb" shell "echo '$*' > $files/tp"; exit 0 ;;
    hide) shift; "$adb" shell "echo '$*' > $files/hide"; exit 0 ;;
esac

before="$("$adb" shell ls "$files/diag" 2>/dev/null | sort | tail -1 || true)"
"$adb" shell touch "$files/diag-request"
for _ in $(seq 1 30); do
    sleep 1
    latest="$("$adb" shell ls "$files/diag" 2>/dev/null | sort | tail -1 || true)"
    if [ -n "$latest" ] && [ "$latest" != "$before" ] && "$adb" shell ls "$files/diag/$latest/report.txt" >/dev/null 2>&1; then
        mkdir -p "$root/work/diag"
        "$adb" pull "$files/diag/$latest" "$root/work/diag/" >/dev/null
        echo "$root/work/diag/$latest"
        exit 0
    fi
done
echo "no snapshot after 30 s: is the game running and not paused?" >&2
exit 1
