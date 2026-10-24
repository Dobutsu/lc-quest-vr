#!/usr/bin/env bash
# What friends need for a test session (docs/multiplayer.md), in work/release (never published: the APK holds the game):
#   lc-quest-vr.apk   the Quest build (Quest 2, 3, 3S, Pro), for friends who own the game (sideload with SideQuest)
#   lc-pico-vr.apk    the same converted for Pico 4 (scripts/make-pico.sh; untested on a Pico)
#   lc-pico-vr-diag.apk  a development build for Pico that logs everything (DiagnosticLog, docs/pico.md), when
#                     work/build/lc-quest-vr-diag.apk exists
#   LanJoin-PC.zip    BepInEx 5 (x64) and the LanJoin plugin, unpacked into the game's folder by PC players who join
#                     someone else's LAN host (the host needs nothing)
#   SteamBridge.zip   the Steam bridge (tools/SteamBridge), for a Quest player who joins a friend's Online lobby: runs
#                     on a Windows PC with Steam logged in as that player
#   scripts/package-friends.sh
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
out="$root/work/release"
bepinex="$root/work/downloads/BepInEx5"
staging="$out/LanJoin-PC"

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
"$DOTNET_ROOT/dotnet" build "$root/tools/LanJoin.Pc" -c Release -v quiet -nologo | { grep -E " error " || true; }

rm -rf "$staging" "$out/LanJoin-PC.zip"
mkdir -p "$staging/BepInEx/plugins"
cp -R "$bepinex/BepInEx/core" "$staging/BepInEx/"
cp "$bepinex/winhttp.dll" "$bepinex/doorstop_config.ini" "$bepinex/changelog.txt" "$staging/"
cp "$root/tools/LanJoin.Pc/bin/Release/netstandard2.1/LcQuest.LanJoin.dll" "$staging/BepInEx/plugins/"
(cd "$staging" && zip -qr "$out/LanJoin-PC.zip" .)
rm -rf "$staging"

rm -f "$out/SteamBridge.zip"
"$DOTNET_ROOT/dotnet" publish "$root/tools/SteamBridge" -c Release -r win-x64 -v quiet -nologo | { grep -E " error " || true; }
# One exe: Steamworks' steam_api64.dll and Avalonia's native libraries are bundled and unpacked on start
(cd "$root/tools/SteamBridge/bin/Release/net8.0/win-x64/publish" && zip -q "$out/SteamBridge.zip" LCQuestSteamBridge.exe)

cp "$root/work/build/lc-quest-vr.apk" "$out/lc-quest-vr.apk"
"$root/scripts/make-pico.sh" >/dev/null
cp "$root/work/pico/lc-pico-ovrp3.3.0.apk" "$out/lc-pico-vr.apk"
if [ -f "$root/work/build/lc-quest-vr-diag.apk" ]; then
    "$root/scripts/make-pico.sh" "$root/work/build/lc-quest-vr-diag.apk" >/dev/null
    cp "$root/work/pico/lc-quest-vr-diag-pico-ovrp3.3.0.apk" "$out/lc-pico-vr-diag.apk"
fi
cp "$root/tools/release/README.txt" "$out/README.txt"
ls -la "$out"
