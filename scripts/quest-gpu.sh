#!/usr/bin/env bash
# GPU cost of rendering switches on the headset: applies each "key value" (unity-tools/Runtime/QuestTuning.cs), waits,
# and prints VrApi's FPS and App (GPU frame) time averaged over a few seconds.
#   scripts/quest-gpu.sh "addShadows 0" "feature:CustomPassFeature 0" ...   (a bare "-" measures without changing)
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
adb="/Applications/Unity/Hub/Editor/2022.3.62f3/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb"

measure() {
    "$adb" logcat -c
    sleep 5
    "$adb" logcat -d | grep -E 'VrApi.*FPS=' | tail -4 |
        sed -E 's/.*FPS=([0-9]+).*App=([0-9.]+)ms.*/\1 \2/' |
        awk '{ f += $1; a += $2; n++ } END { if (n) printf "fps %.0f  app %.1f ms\n", f / n, a / n; else print "no VrApi lines" }'
}

for change in "$@"; do
    [ "$change" != - ] && "$root/scripts/quest-diag.sh" invoke "LCQuest.QuestTuning.Set $change"
    sleep 2
    printf '%-34s ' "$change"; measure
done
