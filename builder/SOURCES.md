# What the kit carries and where its source is

The kit is everything the LC Quest Builder adds to the player's own copy of Lethal Company to make the Quest build. It
contains no part of the game.

| Part | What it is | License | Source |
|---|---|---|---|
| scripts/, unity-tools/ | The port's pipeline scripts and Unity tooling | GPL-3.0 | this repository |
| prebuilt/lcvr/LCVR.dll | LCVR 1.5.0 with the port's patches (patches/lcvr) | GPL-3.0 (licenses/GPL-3.0.txt) | https://github.com/DaXcess/LCVR + patches/lcvr in this repository |
| prebuilt/standalone/LCVR.Standalone.dll | Starts LCVR without BepInEx; Android overrides | GPL-3.0 | tools/LCVR.Standalone in this repository |
| prebuilt/runtime/LCQuest.Runtime.dll | unity-tools/Runtime compiled outside Unity, read by the HDRP shim step | GPL-3.0 | tools/UnityScripts in this repository |
| prebuilt/android/libopus.so | libopus 1.5.2 for Android arm64 | BSD-3-Clause (licenses/opus-BSD-3-Clause.txt) | https://opus-codec.org (scripts/build-opus-android.sh) |
| prebuilt/android/libAudioPluginDissonance.so | A stand-in for Dissonance's desktop-only audio plugin | GPL-3.0 | native/dissonance in this repository (scripts/build-dissonance-android.sh) |

Downloaded by the builder at build time from their publishers (pinned by SHA-256 in kit.json): AssetRipper, uv and
Python with the packages in python/requirements.lock, the LCVR 1.5.0 release (its asset bundles and RuntimeDeps),
BepInEx 5, the NuGet package BepInEx.BaseLib, Unity's built-in shader source; for a Pico build, Eclipse Temurin JRE 17
and ovrport's command-line jar (https://github.com/ovrport/app, GPL-3.0), which fetches its own OpenXR loader
libraries. Unity itself comes through Unity Hub under Unity's terms.
