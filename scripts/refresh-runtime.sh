#!/usr/bin/env bash
# After editing unity-tools: copy our scripts and shaders into work/project without recreating it, and regenerate the
# HDRP shim from the runtime code compiled outside Unity (a new HDRP member in our code must be kept by the shim).
#   scripts/refresh-runtime.sh
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
project="$root/work/project"
python="${PYTHON:-$root/work/tools/venv/bin/python}"  # uv venv with builder/python/requirements.lock
"${DOTNET_ROOT:-$HOME/.dotnet}/dotnet" build "$root/tools/UnityScripts" -c Release -v quiet -nologo | { grep -E " error " || true; }
"$python" "$root/scripts/hdrp_shim.py" "$project" \
    --scan "$root/work/downloads/LCVR-1.5.0/BepInEx/plugins/LCVR/LCVR.dll" \
           "$root/tools/UnityScripts/bin/Release/netstandard2.1/LCQuest.Runtime.dll" \
    --search "$root/work/downloads/BepInEx5/BepInEx/core" | tail -1
cp "$root/unity-tools/Editor/"*.cs "$root/unity-tools/Reference/"*.cs "$project/Assets/LCQuest/Editor/"
# VR projects (make-project.sh without --flat) also have the XR editor scripts
[ -f "$project/Assets/LCQuest/Editor/QuestXRSetup.cs" ] && cp "$root/unity-tools/EditorXR/"*.cs "$project/Assets/LCQuest/Editor/"
cp "$root/unity-tools/Runtime/"*.cs "$project/Assets/LCQuest/Runtime/"
cp "$root/unity-tools/Runtime/Shaders/"*.shader "$project/Assets/LCQuest/Shaders/"
# Soft Mask's shaders (the export's are placeholders) over theirs in Resources, where the DLL loads them by name
cp "$root/unity-tools/SoftMask/"*.shader "$project/Assets/Resources/"
cp "$root/unity-tools/SoftMask/"*.hlsl "$root/unity-tools/SoftMask/"*.cginc "$project/Assets/LCQuest/Shaders/"
for shader in "$root/unity-tools/Shaders/"*.shader; do cp "$shader" "$project/Assets/Shader/$(basename "$shader")"; done
echo "runtime refreshed"
