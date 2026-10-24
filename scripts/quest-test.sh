#!/usr/bin/env bash
# Run the build on the Quest without wearing it and drive it to a test spot (unity-tools/Runtime/QuestDiagnostics.cs).
#   scripts/quest-test.sh [--install] [--flat] [--worn] [menu|ship|moon]   (default ship; --install: work/build/lc-quest-vr.apk,
#                                                                  with --flat lc-quest-flat.apk)
#   scripts/quest-test.sh --restore                      headset back to normal (proximity sensor, Guardian)
# ship: LAN -> Host -> the ship; moon: also lands on Experimentation (the save keeps the last routed moon, so it is
# routed explicitly); moon=<id> routes to that level first (StartOfRound.levels: 0 Experimentation, 1 Assurance, 2 Vow, 4 March, 5 Adamance, 6 Rend,
# 7 Dine, 8 Offense, 9 Titan, 10 Artifice, ...); weather=<n> lands in that weather (LevelWeatherType: -1 none, 0 dust clouds,
# 1 rainy, 2 stormy, 3 foggy, 4 flooded, 5 eclipsed); interior=<n> generates that interior (0 factory, 1 manor,
# 4 mineshaft).
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
adb="/Applications/Unity/Hub/Editor/2022.3.62f3/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb"
package="io.lcvr.quest"
diag="$root/scripts/quest-diag.sh"
target="ship"
apk="lc-quest-vr"
install=0
worn=0
level=""
weather=""
interior=""

for arg in "$@"; do
    case "$arg" in
        --restore)
            "$adb" shell am force-stop "$package"
            "$adb" shell am broadcast -a com.oculus.vrpowermanager.automation_disable >/dev/null
            "$adb" shell setprop debug.oculus.guardian_pause 0
            echo "headset restored"; exit 0 ;;
        --install) install=1 ;;
        --worn) worn=1 ;;
        --flat) apk="lc-quest-flat" ;;
        moon) target=moon; level=0 ;;
        moon=*) target=moon; level="${arg#moon=}" ;;
        weather=*) weather="${arg#weather=}" ;;
        interior=*) interior="${arg#interior=}" ;;
        *) target="$arg" ;;
    esac
done

# Changes still waiting to be seen on the headset (docs/headset-checks.md), shown once the game is at the test spot
open_checks() {
    local checks
    checks="$(grep -E '^## |^- \[ \]' "$root/docs/headset-checks.md" 2>/dev/null | grep -B1 '^- \[ \]' | grep -v '^--$' || true)"
    [ -n "$checks" ] && printf '\n== open headset checks (docs/headset-checks.md):\n%s\n' "$checks"
    return 0
}
trap open_checks EXIT

[ "$install" = 1 ] && "$adb" install -r -g "$root/work/build/$apk.apk" | tail -1

# Keep the headset awake and unpaused while nobody wears it; Guardian's dialog would block the launch. --worn: someone
# wears it, the headset stays as it is
if [ "$worn" = 0 ]; then
    "$adb" shell am broadcast -a com.oculus.vrpowermanager.prox_close >/dev/null
    "$adb" shell setprop debug.oculus.guardian_pause 1
fi
"$adb" shell input keyevent KEYCODE_WAKEUP
"$adb" shell am force-stop "$package"
# A "controllers required" dialog left from an earlier launch blocks every later one (development builds declare hand
# tracking so the check passes, unity-tools/EditorXR/DevHandTrackingManifest.cs)
for task in $("$adb" shell dumpsys activity activities | sed -n 's/.*LaunchCheck[A-Za-z]*DialogActivity t\([0-9]*\).*/\1/p' | sort -u); do
    "$adb" shell cmd activity stack remove "$task" || true
done
# The game near the memory limit can get a system app killed: its "isn't responding" dialog blocks launches too
"$adb" shell uiautomator dump /sdcard/ui.xml >/dev/null 2>&1 || true
wait_button="$("$adb" shell cat /sdcard/ui.xml 2>/dev/null | tr '>' '\n' | grep 'text="Wait"' | grep -oE 'bounds="[^"]+"' | grep -oE '[0-9]+' | tr '\n' ' ' || true)"
if [ -n "$wait_button" ]; then
    read -r x1 y1 x2 y2 <<<"$wait_button"
    "$adb" shell input tap $(( (x1 + x2) / 2 )) $(( (y1 + y2) / 2 ))
    echo "dismissed a system 'isn't responding' dialog"
fi
"$adb" logcat -c
"$adb" shell am start -n "$package/com.unity3d.player.UnityPlayerActivity" >/dev/null
sleep 25
[ "$target" = menu ] && exit 0

if [ "$apk" = lc-quest-vr ]; then
    "$diag" click "Menu Canvas/LAN"; sleep 10   # LCVR's launch scene, then the boot screen
else
    "$diag" click LANButton
    "$diag" click "LANWarning/Panel/Confirm"; sleep 1
fi
"$diag" click ResponseButton || true   # the news panel shows once per version
"$diag" click HostButton; sleep 1
"$diag" click "HostSettingsContainer/Confirm"; sleep 35
[ "$target" = ship ] && exit 0

# A fixed map seed: the same facility every run (scripts/quest-bench.sh measures in its rooms)
"$diag" invoke "QuestDiagnostics.FixSeed 4242"
if [ -n "$level" ]; then "$diag" invoke "StartOfRound.ChangeLevelServerRpc $level 60"; sleep 20; fi   # the ship flies there first
[ -n "$weather" ] && "$diag" invoke "QuestDiagnostics.Weather $weather"
[ -n "$interior" ] && "$diag" invoke "QuestDiagnostics.Interior $interior"
"$diag" invoke StartMatchLever.StartGame; sleep 50
