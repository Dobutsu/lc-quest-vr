#!/usr/bin/env bash
# GPU frame at fixed views inside the facility, with nobody wearing the headset: "tp node <fraction> <yaw>" turns the VR
# view too (QuestDiagnostics.TurnView), so the same view can be measured again. The game must already be on a moon
# (scripts/quest-test.sh moon=<id>) and the profiler's detailed mode on (see scripts/quest-trace.sh).
#   scripts/quest-views.sh scan [fractions...]      every fraction (default 0.1 .. 0.9) at yaw 0/90/180/270: one median
#                                                   frame each, to find the heavy views
#   scripts/quest-views.sh ab "<view>"... -- "<key> <test> <default>"...
#                                                   at each view ("0.4 90": node fraction and yaw): the frame as it is,
#                                                   then each QuestTuning key at its test value and back to its default
# Each switch waits 8 s for the frame to settle. On a development build the profiler layer crashes the game within
# minutes (scripts/build-apk.sh): measure on a build made with -adbLaunch instead of -developmentBuild.
set -uo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
adb="/Applications/Unity/Hub/Editor/2022.3.62f3/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb"
diag="$root/scripts/quest-diag.sh"

frame() {
    perl -e 'alarm 45; exec {$ARGV[0]} @ARGV' "$root/scripts/quest-trace.sh" | sed 's/^- *//'
}

alive() {
    "$adb" shell pidof io.lcvr.quest >/dev/null || { echo "game is gone (crash log: adb logcat -b crash)"; exit 1; }
}

# Waits up to 15 s for the game to log a line with the text: an unworn headset pauses the game after a while (the
# frames still come, the switches and teleports no longer apply), which would make every later number meaningless
confirm() {
    for _ in $(seq 15); do
        "$adb" logcat -d -s Unity | grep -qF "$1" && return 0
        sleep 1
    done
    echo "the game did not answer ($1): paused? restart it (scripts/quest-test.sh moon=<id>)"; exit 1
}

view() {
    "$adb" logcat -c
    "$diag" tp "node $1 $2" >/dev/null
    confirm "view turned to yaw $2"
    sleep 8
}

set_() {
    "$adb" logcat -c
    "$diag" invoke "LCQuest.QuestTuning.Set $1 $2"
    confirm "[QuestTuning] $1 = $2"
    sleep 8
}

case "${1:-}" in scan|ab) "$diag" tp enter >/dev/null; sleep 8 ;; esac   # inside the facility, as a player gets there
case "${1:-}" in
    scan)
        shift
        fractions=("$@")
        [ ${#fractions[@]} -eq 0 ] && fractions=(0.1 0.2 0.3 0.4 0.5 0.6 0.7 0.8 0.9)
        for f in "${fractions[@]}"; do
            for yaw in 0 90 180 270; do
                view "$f" "$yaw"
                printf 'node %-5s yaw %-4s ' "$f" "$yaw"; frame
                alive
            done
        done ;;
    ab)
        shift
        views=()
        while [ $# -gt 0 ] && [ "$1" != -- ]; do views+=("$1"); shift; done
        shift
        for v in "${views[@]}"; do
            read -r f yaw <<<"$v"
            view "$f" "$yaw"
            echo "== node $f yaw $yaw"
            printf '%-36s ' "as is"; frame
            for change in "$@"; do
                read -r key test default <<<"$change"
                set_ "$key" "$test"
                printf '%-36s ' "$key $test"; frame
                alive
                set_ "$key" "$default"
                printf '%-36s ' "$key $default"; frame
                alive
            done
        done ;;
    *) sed -n '2,11p' "$0"; exit 1 ;;
esac
