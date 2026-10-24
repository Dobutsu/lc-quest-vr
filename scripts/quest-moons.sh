#!/usr/bin/env bash
# A tour of moons on the Quest, from the ship in orbit (scripts/quest-test.sh ship first): for each level id, route
# the ship there, land, snapshot outside the ship and inside the facility, take off again.
#   scripts/quest-moons.sh <level id> [<level id> ...]    (ids: the "level" line of a quest-diag.sh report)
# -> work/diag/<time>/ per snapshot; a summary line per moon
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
diag="$root/scripts/quest-diag.sh"

for level in "$@"; do
    "$diag" invoke StartOfRound.ChangeLevel "$level"; sleep 8
    "$diag" invoke StartMatchLever.StartGame; sleep 60
    "$diag" tp -7.67 0.29 -14.39 180; sleep 5
    outside="$("$diag")"
    "$diag" tp enter; sleep 8
    inside="$("$diag")"
    echo "level $level: outside $outside | inside $inside | $(grep -m1 '^frame' "$inside/report.txt" || true)"
    "$diag" tp exit; sleep 3
    "$diag" tp ship; sleep 2
    "$diag" invoke StartMatchLever.EndGame; sleep 45
done
