#!/usr/bin/env bash
# A video of the game for showing it, with the headset's own recorder (one eye, undistorted, with sound). The recorder
# only takes commands from the system UI (over adb: "Missing result messenger"), so the player starts and stops it:
# the universal menu's camera, or holding the Meta button and pulling a trigger. The game must be on a moon already
# (scripts/quest-test.sh --worn moon=<id>).
#   scripts/quest-tour.sh [<seconds per spot>]   teleports through the moon's spots, no enemies
#   scripts/quest-tour.sh --pull                 copies the newest recording to work/video
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
adb="/Applications/Unity/Hub/Editor/2022.3.62f3/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb"
dir=/sdcard/Oculus/VideoShots

if [ "${1:-}" = --pull ]; then
    latest="$("$adb" shell ls -t "$dir" | head -1)"
    mkdir -p "$root/work/video"
    "$adb" pull "$dir/$latest" "$root/work/video/" >/dev/null
    echo "$root/work/video/$latest"
    exit 0
fi

dwell="${1:-12}"
for spot in ship outside enter "node 0.3" "node 0.5" "node 0.75" "node 0.9" exit; do
    "$root/scripts/quest-diag.sh" tp $spot
    sleep "$dwell"
done
echo "tour done"
