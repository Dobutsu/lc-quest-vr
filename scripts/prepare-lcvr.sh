#!/usr/bin/env bash
# LCVR as the Quest build uses it: external/LCVR (v1.5.0, left untouched) plus patches/lcvr/*.patch, built in
# work/lcvr-src.
#   scripts/prepare-lcvr.sh   -> work/lcvr-src/bin/Release/netstandard2.1/LCVR.dll
# Patches (in order):
#   0001-null-actions-at-runtime  InputPatches reads NullActions when PlayerActions is built (bundle not loaded at bake time)
#   0002-spectator-voice-transpiler  UpdatePlayerVoiceEffects matched by pattern (V81: "set2D = false" hit the player object)
#   0003-android-standalone  Quest build: no Win32 dialogs/registry/hash check, OpenXR features added to the APK's, no HDRP asset
#   0004-no-mod-compatibility  MoreCompany/CullFactory compatibility removed (no mods on Quest; IL2CPP's linker needs their DLLs)
#   0005-no-hdrp-default-volume  the VR launch scene works without HDRP's hidden "Default Volume" object
#   0006-android-lan-only  the VR launch screen offers only LOCAL (no Steam on the headset: Online hung in InitScene)
#   0007-hud-further-away  tips, notifications and the face HUD at 0.75 m instead of 0.5 m, scaled to keep their size
#   0008-forearm-twist  part of the wrist's roll moves onto the forearm (no twist bones: the wrist wrung like a candy wrapper)
#   0009-quest-controller-bindings  Quest 3's Touch Plus controllers get the Meta Quest scheme (with none, every controller's
#        bindings worked at once); X menu, Y scan, left trigger sprint, left menu button height reset
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
src="$root/work/lcvr-src"
dotnet="${DOTNET:-$HOME/.dotnet/dotnet}"

# Fresh sources on every run; bin/ and obj/ stay for incremental builds
mkdir -p "$src"
rsync -a --delete --exclude .git --exclude bin --exclude obj --exclude Preloader "$root/external/LCVR/" "$src/"
shopt -s nullglob
for patch in "$root"/patches/lcvr/*.patch; do
    patch -p1 -E --forward --silent -d "$src" < "$patch" || { echo "patch failed: $(basename "$patch")" >&2; exit 1; }
    echo "applied $(basename "$patch")"
done

"$dotnet" build -c Release "$src/LCVR.csproj" -nologo -v quiet
echo "LCVR built: $src/bin/Release/netstandard2.1/LCVR.dll"
