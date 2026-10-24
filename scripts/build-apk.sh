#!/usr/bin/env bash
# Unity part of the Quest build on the Mac: Prepare (Android settings, URP, materials), then BuildApk, in two editor
# runs (settings changes need a domain reload before BuildPlayer runs).
#   scripts/build-apk.sh [<apk name>] [-developmentBuild] [-adbLaunch] [--prepare-only | --build-only]
# -> work/build/<apk name>.apk (default lc-quest-flat); logs in work/logs/unity-prepare.log, unity-build.log
# Measure GPU time on a build without -developmentBuild: the development player marks its GPU work with Vulkan debug
# labels, and the headset's profiler layer (ovrgpuprofiler's detailed mode) crashes on them within minutes. -adbLaunch:
# a release build that still starts over adb with the controllers asleep (development builds always do; see
# unity-tools/EditorXR/DevHandTrackingManifest.cs).
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
unity="/Applications/Unity/Hub/Editor/2022.3.62f3/Unity.app/Contents/MacOS/Unity"
project="$root/work/project"
name="lc-quest-flat"
prepare=1; build=1; extra=()
while [ $# -gt 0 ]; do
    case "$1" in
        --prepare-only) build=0 ;;
        --build-only) prepare=0 ;;
        -developmentBuild) extra+=(-developmentBuild) ;;
        -adbLaunch) extra+=(-adbLaunch) ;;
        *) name="$1" ;;
    esac
    shift
done
mkdir -p "$root/work/logs" "$root/work/build"

if [ "$prepare" = 1 ]; then
    echo "== Unity: prepare (log: work/logs/unity-prepare.log)"
    "$unity" -batchmode -nographics -timestamps -projectPath "$project" -buildTarget Android \
        -executeMethod LCQuest.Editor.QuestBuild.Prepare -logFile "$root/work/logs/unity-prepare.log"
fi
if [ "$build" = 1 ]; then
    echo "== Unity: build (log: work/logs/unity-build.log)"
    "$unity" -batchmode -nographics -timestamps -projectPath "$project" -buildTarget Android \
        -executeMethod LCQuest.Editor.QuestBuild.BuildApk -apkPath "$root/work/build/$name.apk" ${extra[@]+"${extra[@]}"} \
        -logFile "$root/work/logs/unity-build.log"
    ls -la "$root/work/build/$name.apk"
fi
