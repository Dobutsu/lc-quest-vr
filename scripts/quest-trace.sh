#!/usr/bin/env bash
# One frame's GPU time per render pass (surface), from the headset's render stage trace (ovrgpuprofiler): steadier
# than VrApi's App= time for comparing changes; the time the compositor preempts the GPU is left out. Needs the driver's detailed mode, set before the game starts:
#   adb shell ovrgpuprofiler -e io.lcvr.quest   (then restart the game; -d turns it off)
# Each trace leaves ~56 MB of graphics memory in the game's process (the driver's profiling buffers): many traces in one
# run can push the game into the memory killer; restart it between series.
#   scripts/quest-trace.sh ["key value" ...]   applies QuestTuning switches first (as quest-gpu.sh), one trace after each
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
adb="/Applications/Unity/Hub/Editor/2022.3.62f3/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb"

trace() {
    # 0.5 s holds several frames: a frame starts at an eye-sized pass that clears (no LoadColor stage: the main pass;
    # passes after a full-screen effect load the picture back); the median frame is printed
    "$adb" shell ovrgpuprofiler --trace=0.5 2>/dev/null | python3 -c '
import re, sys
surfaces, preempt = [], []
for line in sys.stdin:
    m = re.match(r"Surface\s+\d+\s+\|\s*(\d+)\s*x\s*(\d+)\s*\|(.*?)\|.*\|\s*([\d.]+) ms(.*)", line)
    if m:
        w, h, fmt, ms, stages = int(m[1]), int(m[2]), m[3], float(m[4]), m[5]
        # The compositor preempts the GPU (~0.5 ms each time, at random points of the frame): its time is left out
        p = re.search(r"Preempt : ([\d.]+)ms", stages)
        if p: ms -= float(p[1]); preempt.append(float(p[1]))
        kind = "shadow" if "color 0 bit" in fmt else "eye" if w >= 1000 and h >= 800 else "half" if w >= 500 else "small"
        start = kind == "eye" and "HwBinning" in fmt and "LoadColor" not in stages
        surfaces.append((kind, w, h, ms, start))
frames, current = [], []
for s in surfaces:
    if s[4]:
        frames.append(current); current = []
    current.append(s)
# The first chunk is a partial frame; each frame runs from one main pass to the next (shadow maps of the next frame,
# drawn before its main pass, count in this one: the same work per frame)
frames = frames[1:]
if not frames: print("no frames traced (detailed mode on? game running?)"); sys.exit()
totals = [sum(s[3] for s in f) for f in frames]
frame = frames[totals.index(sorted(totals)[len(totals) // 2])]
by = {}
for kind, w, h, ms, _ in frame: by[kind] = by.get(kind, 0) + ms
print(f"frame {sum(by.values()):5.1f} ms ({len(frames)} traced, compositor {sum(preempt) / len(frames):.1f} left out) | " + "  ".join(f"{k} {v:.1f}" for k, v in sorted(by.items())) +
      " | eye passes: " + " ".join(f"{ms:.1f}" for kind, w, h, ms, _ in frame if kind == "eye"))
'
}

if [ $# -eq 0 ]; then printf "%-32s " -; trace; exit 0; fi
for change in "$@"; do
    [ "$change" != - ] && "$root/scripts/quest-diag.sh" invoke "LCQuest.QuestTuning.Set $change"
    sleep 3
    printf '%-32s ' "$change"; trace
done
