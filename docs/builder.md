# LC Quest Builder: the players' builder

_Started 2026-10-24, from the builder of the REPO port ([repo-quest-vr](https://github.com/Dobutsu/repo-quest-vr), its docs/builder.md has the design in full: the
window, downloads, Unity through the Hub, the Windows specifics, updates, installing and uninstalling)._ A Windows
program that builds the Quest version of Lethal Company from the player's own copy of the game: the player runs
`LCQuestBuilder.exe`, it finds the game, downloads what it needs, builds the APK and installs it on the headset. The
players' instructions are `builder/release/README.txt` (in the zip). Windows only; our builds on the Mac keep using the
scripts (or `QuestBuilder.Cli build` for testing the pipeline).

## What differs from REPO's

**The pipeline** (`builder/QuestBuilder.Core/Pipeline`) follows this port's scripts:

| Step | What it does | Script it follows |
|---|---|---|
| export | AssetRipper on the game, then on a minimal game folder with LCVR.dll and LCVR's two asset bundles | export.sh, export-lcvr.sh |
| bake | HarmonyBaker for Android: LCVR's patches, the Android overrides of LCVR.Standalone, BepInEx's 0Harmony rewritten, Input System hooked at call sites | bake.sh all android |
| project | the export + baked DLLs, LCVR's assets and runtime, package swap, LCVR shaders, package patches, HDRP shim, assembly renames, scenes, textures, our native libraries, shaders and scripts | make-project.sh |
| unity | `LCQuest.Editor.QuestBuild.Prepare`, then `.BuildApk` | build-apk.sh |
| pico | ovrport on the Quest APK | make-pico.sh |

Checked on 2026-10-24: the builder's export, bake and project steps on the Mac, run on the same export as
`make-project.sh`, give a byte-identical project (Library aside).

**The kit** (`scripts/make-kit.sh` → `work/kit`, ~2 MB): scripts, unity-tools, LCVR.dll with patches/lcvr,
LCVR.Standalone.dll, `LCQuest.Runtime.dll` (unity-tools/Runtime compiled outside Unity: the HDRP shim keeps what it
uses), and our arm64 `libopus.so` and `libAudioPluginDissonance.so`, so a player needs neither the NDK nor the .NET
SDK. LCVR's asset bundles come from the LCVR 1.5.0 release the builder downloads (GitHub), not from the kit.

**No Steam client part:** REPO's builder carried SteamShim/SteamKit2 and warned about the account; here the headset runs
in LAN mode, and Online lobbies go through the Steam bridge (`tools/SteamBridge`, a separate download on the release
page, docs/multiplayer.md). The window's note on playing with friends says so.

**The HDRP shim on a first build:** `make-project.sh` scans our runtime code only once a Library exists (it compiles
it against Unity's package assemblies). The builder scans the prebuilt DLL every time; without it a first build's
shim missed HdrpBridge's members (`fadeDistance`, `volumetricDimmer`...) and the scripts did not compile.

## Releasing

`scripts/release.sh <version>` (from the REPO port): the kit, the tools zip and the window's exe for win-x64, signed
`latest.json`. Release key: `~/.config/lcquest/release-signing-key.pem` (made on the first release; back it up), public
half `builder/release-public-key.txt`. Before anything goes public: `scripts/publish_check.py` on the tree, the history
that is pushed, and the zips (it needs `work/game` or `--game`).

**Published 2026-10-24:** the public repository https://github.com/Dobutsu/lc-quest-vr (GPL-3.0; one fresh commit,
the full history stays in the private lc-quest) and v0.1.0 as a pre-release with the builder and the Steam bridge. The
public history is made from this repository's tree (a clone, `git checkout --orphan`, one commit as
`39274204+Dobutsu@users.noreply.github.com`) after `publish_check.py --history` passes. Not written to: Zeekerss or
DaXcess (the author's decision). Open: a full build on Windows.
