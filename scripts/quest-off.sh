#!/usr/bin/env bash
# After testing: close the game, give the headset back its proximity sensor and Guardian, and put it to sleep (a long
# session drains it even on the Mac's USB power; asleep it stays on adb, where a switched-off headset needs the
# power button and a slow boot).
#   scripts/quest-off.sh
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
adb="/Applications/Unity/Hub/Editor/2022.3.62f3/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb"
"$adb" get-state >/dev/null 2>&1 || { echo "no headset connected"; exit 0; }
"$root/scripts/quest-test.sh" --restore
"$adb" shell ovrgpuprofiler -d >/dev/null 2>&1 || true
"$adb" shell input keyevent KEYCODE_SLEEP
echo "headset asleep"
