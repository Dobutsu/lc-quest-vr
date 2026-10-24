#!/usr/bin/env bash
# AudioPluginDissonance for Android arm64 (native/dissonance): Dissonance's capture preprocessor without WebRTC, built
# with Unity's NDK. Dissonance ships the library for desktop only; without it voice capture stops on the first frame.
#   scripts/build-dissonance-android.sh   -> work/dissonance-android/libAudioPluginDissonance.so
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
out="$root/work/dissonance-android"
ndk="/Applications/Unity/Hub/Editor/2022.3.62f3/PlaybackEngines/AndroidPlayer/NDK/toolchains/llvm/prebuilt/darwin-x86_64/bin"
lib="$out/libAudioPluginDissonance.so"

mkdir -p "$out"
"$ndk/aarch64-linux-android29-clang" -shared -O2 -fPIC -fvisibility=hidden -Wall -Wno-unused-parameter -o "$lib" \
    "$root/native/dissonance/audio_plugin_dissonance.c" \
    -Wl,-soname,libAudioPluginDissonance.so -Wl,-z,max-page-size=16384 -lm
"$ndk/llvm-strip" --strip-unneeded "$lib"

# Everything DissonanceVoip imports from AudioPluginDissonance
for fn in Dissonance_CreateRnnoiseState Dissonance_DestroyRnnoiseState Dissonance_RnnoiseProcessFrame \
          Dissonance_RnnoiseGetGains Dissonance_CreatePreprocessor Dissonance_DestroyPreprocessor \
          Dissonance_ConfigureNoiseSuppression Dissonance_ConfigureVadSensitivity Dissonance_ConfigureAecSuppression \
          Dissonance_GetVadSpeechState Dissonance_PreprocessCaptureFrame Dissonance_PreprocessorExchangeInstance \
          Dissonance_GetFilterState Dissonance_GetAecMetrics Dissonance_SetAgcIsOutputMutedState; do
    "$ndk/llvm-nm" -D --defined-only "$lib" | grep -q " T $fn$" || { echo "missing export $fn" >&2; exit 1; }
done
echo "built $lib"
