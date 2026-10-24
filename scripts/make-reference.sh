#!/usr/bin/env bash
# A second Unity project from the same export that keeps HDRP (the real 14.0.12 package in place of the DLL), for
# reference images of what the PC game renders: same scenes, materials, lights, volumes and fog. Rendered on the Mac
# (Metal) by unity-tools/Reference/ReferenceRender.cs; scripts/reference-render.sh runs it.
#   scripts/make-reference.sh   -> work/reference
# Only the game's own shaders (Shader Graphs, the outline pass) stay AssetRipper placeholders there.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
work="$root/work"
export_dir="$work/export/ExportedProject"
project="$work/reference"
python="${PYTHON:-$root/work/tools/venv/bin/python}"  # uv venv with builder/python/requirements.lock

if [ -d "$project/Library" ]; then rm -rf "$work/Library.reference.keep"; mv "$project/Library" "$work/Library.reference.keep"; fi
rm -rf "$project"
cp -cR "$export_dir" "$project"
[ -d "$work/Library.reference.keep" ] && mv "$work/Library.reference.keep" "$project/Library"
printf 'm_EditorVersion: 2022.3.62f3\nm_EditorVersionWithRevision: 2022.3.62f3 (96770f904ca7)\n' > "$project/ProjectSettings/ProjectVersion.txt"
sed -i '' 's/^  m_RefreshImportMode: [0-9]*$/  m_RefreshImportMode: 1/' "$project/ProjectSettings/EditorSettings.asset"

"$python" "$root/scripts/package_swap.py" "$project" --pipeline hdrp
"$python" "$root/scripts/rename_game_assemblies.py" "$project"
mkdir -p "$project/Assets/LCQuest/Editor"
cp "$root/unity-tools/Reference/"*.cs "$project/Assets/LCQuest/Editor/"
echo "reference project ready: $project"
