#!/usr/bin/env bash
# The Quest builder's kit in work/kit: everything that turns a player's own copy of the game into the Quest project,
# except the game. builder/QuestBuilder.Core/Kit.cs reads it; scripts/release.sh embeds it into the builder.
# From the REPO port's make-kit.sh.
#   scripts/make-kit.sh [version]      (default version: dev)
# Contents:
#   kit.json, python/requirements.lock      builder/ (pinned downloads, Unity version, Python packages)
#   scripts/, unity-tools/                  the pipeline's Python scripts and Unity tooling from this checkout
#   prebuilt/lcvr/LCVR.dll                  LCVR v1.5.0 + patches/lcvr (scripts/prepare-lcvr.sh)
#   prebuilt/standalone/                    tools/LCVR.Standalone (the bootstrap and the Android overrides)
#   prebuilt/runtime/LCQuest.Runtime.dll    unity-tools/Runtime compiled outside Unity (tools/UnityScripts), which
#                                           the HDRP shim scans
#   prebuilt/android/                       libopus.so and libAudioPluginDissonance.so (scripts/build-opus-android.sh,
#                                           build-dissonance-android.sh)
#   licenses/, SOURCES.md                   licenses of what the kit carries and where its sources are
# The checks at the end refuse anything that looks like game files.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
kit="$root/work/kit"
version="${1:-dev}"
dotnet="${DOTNET:-$HOME/.dotnet/dotnet}"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"

"$root/scripts/prepare-lcvr.sh" >/dev/null
"$dotnet" build -c Release -v quiet -nologo "$root/tools/LCVR.Standalone" >/dev/null
"$dotnet" build -c Release -v quiet -nologo "$root/tools/UnityScripts" >/dev/null
[ -f "$root/work/opus-android/libopus.so" ] || "$root/scripts/build-opus-android.sh"
[ -f "$root/work/dissonance-android/libAudioPluginDissonance.so" ] || "$root/scripts/build-dissonance-android.sh"

rm -rf "$kit"
mkdir -p "$kit"/{scripts/lib,python,prebuilt/{lcvr,standalone,runtime,android},licenses}
python3 - "$root/builder/kit.json" "$kit/kit.json" "$version" <<'EOF'
import json, sys
manifest = json.load(open(sys.argv[1]))
manifest["version"] = sys.argv[3]
open(sys.argv[2], "w").write(json.dumps(manifest, indent=2) + "\n")
EOF
cp "$root/builder/python/requirements.lock" "$kit/python/"
# Every pipeline script; not the Mac-only diagnostics, kit and release tooling
for script in "$root"/scripts/*.py; do
    case "$(basename "$script")" in
        inventory.py|shader_disasm.py|publish_check.py|compare_images.py|fit_post_curve.py|package_versions.py) ;;
        *) cp "$script" "$kit/scripts/" ;;
    esac
done
cp "$root"/scripts/lib/*.py "$kit/scripts/lib/"
rsync -a --exclude .DS_Store "$root/unity-tools/" "$kit/unity-tools/"

cp "$root/work/lcvr-src/bin/Release/netstandard2.1/LCVR.dll" "$kit/prebuilt/lcvr/"
cp "$root/tools/LCVR.Standalone/bin/Release/netstandard2.1/LCVR.Standalone.dll" "$kit/prebuilt/standalone/"
cp "$root/tools/UnityScripts/bin/Release/netstandard2.1/LCQuest.Runtime.dll" "$kit/prebuilt/runtime/"
cp "$root/work/opus-android/libopus.so" "$root/work/dissonance-android/libAudioPluginDissonance.so" "$kit/prebuilt/android/"

cp "$root/external/LCVR/LICENSE" "$kit/licenses/GPL-3.0.txt"
cp "$root/work/downloads/opus-1.5.2/COPYING" "$kit/licenses/opus-BSD-3-Clause.txt"
cp "$root/builder/licenses/"* "$kit/licenses/" 2>/dev/null || true
[ -f "$root/builder/SOURCES.md" ] && cp "$root/builder/SOURCES.md" "$kit/"

# Checks: text written with "\n" on every OS (Windows Python would write "\r\n"), and nothing from the game
bad_writes="$(grep -nE 'write_text\(|open\([^)]*"w' "$kit"/scripts/*.py | grep -v 'newline=' || true)"
[ -z "$bad_writes" ] || { echo "kit scripts write text without newline=\"\\n\":" >&2; echo "$bad_writes" >&2; exit 1; }
suspicious="$(find "$kit" \( -name '*.assets' -o -name '*.resS' -o -name '*.resource' -o -name 'Assembly-CSharp*' \
    -o -name 'Lethal Company_Data' -o -name 'globalgamemanagers*' -o -name 'level[0-9]*' -o -name '*.bundle' \
    -o -name 'lethalcompanyvr*' \) -print)"
[ -z "$suspicious" ] || { echo "game or mod files in the kit:" >&2; echo "$suspicious" >&2; exit 1; }

echo "kit $version: $kit ($(du -sh "$kit" | cut -f1))"
