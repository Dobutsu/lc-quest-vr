# Pico 4

The Pico build is the Quest build, converted, as in the REPO port (its docs/pico.md has the details and history).

## Converting (scripts/make-pico.sh)

`scripts/make-pico.sh` runs [ovrport](https://github.com/ovrport/app) 1.2.3's command-line jar (GPL-3.0) on
`work/build/lc-quest-vr.apk`, with a Java 17 runtime (Unity brings 11), and writes
`work/pico/lc-pico-ovrp3.3.0.apk`. ovrport:

- swaps `libopenxr_loader.so` for its own (instance 3.3.0), which picks Pico's OpenXR runtime by
  `ro.product.brand` and maps the Touch controller bindings to `bytedance/pico4(s)_controller`;
- adds the manifest entries Pico needs (`pvr.app.type=vr`) and leaves the game's own libraries alone;
- signs with a key it makes once in `work/pico/ovrport-ws/signatures`: keep that folder, so a later APK installs
  over the earlier one.

The jar and the JRE are in `work/pico/tools` (copied from the REPO port's; the script downloads them otherwise).
`PICO_DEBUGGABLE=0` leaves out ovrport's debuggable mark (Android runs debuggable apps slower).

## The microphone prompt (unity-tools/Runtime/MicPermission.cs)

From the REPO port: Unity asks for the microphone as the game starts, while the OpenXR session is still coming up,
and on Pico 4 the system's permission window then crashed Unity's OpenXR plugin. `Editor/SkipPermissionsManifest.cs`
turns Unity's request off; `MicPermission` asks once per install, on Pico in the main menu after five seconds of
steady XR, on Quest right away. Not checked on Pico yet: whether voice works right after a late grant or only from
the next launch.

## The diagnostic build (unity-tools/Runtime/DiagnosticLog.cs)

For a tester with a Pico and no adb: a development build that logs everything to files on the headset.

    scripts/build-apk.sh lc-quest-vr-diag -developmentBuild
    scripts/make-pico.sh work/build/lc-quest-vr-diag.apk    -> work/pico/lc-quest-vr-diag-pico-ovrp3.3.0.apk

Each start writes `Android/data/io.lcvr.quest/files/logs/<start time>/` (the last five starts are kept):
`game.txt` (every Unity message with stack traces for errors, the device and GPU, and every 5 s the frame rate,
CPU/GPU frame times, memory, XR state and scene; scene loads, focus and pause) and `logcat.txt` (Android's log of the
game's process, with native crashes, the OpenXR runtime's and the graphics driver's messages). The tester connects the
Pico to a PC over USB (allow file access on the headset) and copies the `logs` folder. A release build logs the same
way when a file `diagnostic-log` is put in `files/`.

## Not ported (yet)

- **The GPU hang on Pico 4 (Adreno 650):** REPO's `ShaderWarmup` lets renderers in a few at a time. Port it if
  LC hangs there too (`kgsl-3d0: GPU hang detected` in the kernel log, XR lost in the menu).
- **Untested:** no Pico headset has run this build.
