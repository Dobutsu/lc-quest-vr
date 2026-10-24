#!/usr/bin/env bash
# GPU frame at fixed spots on a moon with scripts/quest-trace.sh, after landing from a fresh start: the ship, outside
# it, the facility's main entrance, three rooms inside (AI nodes at 0.3, 0.5 and 0.75 of the list; 0.25 put the head through a wall on Experimentation),
# then the last room with enemies spawned in front of the player. The headset should lie level, looking ahead.
#   scripts/quest-bench.sh [--install] [--level <id>] [--weather <n>] [<label>]   (weather: see quest-test.sh)
#   -> work/logs/bench.txt (appended), work/diag/vr/bench-<label>-<spot>.jpg
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
diag="$root/scripts/quest-diag.sh"
install=""; level=0; weather=""; label="$(date +%H%M)"
while [ $# -gt 0 ]; do
    case "$1" in
        --install) install=--install ;;
        --level) level="$2"; shift ;;
        --weather) weather="weather=$2"; shift ;;
        *) label="$1" ;;
    esac
    shift
done

"$root/scripts/quest-test.sh" $install "moon=$level" $weather >/dev/null 2>&1
mkdir -p "$root/work/logs"

measure() {
    printf '%-12s %-3s %-12s ' "$label" "${level:-0}" "$1" | tee -a "$root/work/logs/bench.txt"
    "$root/scripts/quest-trace.sh" | sed -E 's/^- +//' | tee -a "$root/work/logs/bench.txt"
    "$root/scripts/quest-shot.sh" "bench-$label-$1" >/dev/null || true
}

for spot in ship outside enter "node 0.3" "node 0.5" "node 0.75"; do
    [ "$spot" = enter ] || "$diag" tp $spot
    [ "$spot" = enter ] && "$diag" tp enter
    sleep 8
    measure "${spot// /}"
done
# Measured at once: they walk off within seconds
for enemy in Flowerman Hoarding_bug Spring Nutcracker; do "$diag" invoke "QuestDiagnostics.Spawn $enemy 3"; done
sleep 1.5
measure enemies
