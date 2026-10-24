#!/usr/bin/env bash
# libopus for Android arm64 as Dissonance imports it ("opus" -> libopus.so): upstream libopus (BSD-3-Clause, xiph.org)
# built with Unity's NDK, plus the four fixed-argument ctl wrappers Dissonance's own build adds (native/opus).
#   scripts/build-opus-android.sh   -> work/opus-android/libopus.so
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
version="1.5.2"
src="$root/work/downloads/opus-$version"
out="$root/work/opus-android"
ndk="/Applications/Unity/Hub/Editor/2022.3.62f3/PlaybackEngines/AndroidPlayer/NDK/toolchains/llvm/prebuilt/darwin-x86_64/bin"

if [ ! -d "$src" ]; then
    mkdir -p "$root/work/downloads"
    curl -sSfL "https://downloads.xiph.org/releases/opus/opus-$version.tar.gz" | tar xz -C "$root/work/downloads"
fi

cd "$src"
if [ ! -f .libs/libopus.a ]; then
    ./configure --host=aarch64-linux-android \
        CC="$ndk/aarch64-linux-android29-clang" AR="$ndk/llvm-ar" RANLIB="$ndk/llvm-ranlib" STRIP="$ndk/llvm-strip" \
        CFLAGS="-O2 -fPIC" --disable-shared --enable-static --with-pic --disable-doc --disable-extra-programs \
        > "$root/work/logs/opus-configure.log" 2>&1
    make -j8 > "$root/work/logs/opus-make.log" 2>&1
fi

mkdir -p "$out"
"$ndk/aarch64-linux-android29-clang" -shared -O2 -fPIC -I include -o "$out/libopus.so" \
    "$root/native/opus/dissonance_opus.c" \
    -Wl,--whole-archive .libs/libopus.a -Wl,--no-whole-archive \
    -Wl,-soname,libopus.so -Wl,-z,max-page-size=16384 -lm
"$ndk/llvm-strip" --strip-unneeded "$out/libopus.so"

# Everything DissonanceVoip imports from opus
for fn in opus_encoder_create opus_encoder_destroy opus_encode_float opus_decoder_create opus_decoder_destroy \
          opus_decode_float opus_get_version_string opus_pcm_soft_clip dissonance_opus_encoder_ctl_in \
          dissonance_opus_encoder_ctl_out dissonance_opus_decoder_ctl_in dissonance_opus_decoder_ctl_out; do
    "$ndk/llvm-nm" -D --defined-only "$out/libopus.so" | grep -q " T $fn$" || { echo "missing export $fn" >&2; exit 1; }
done
echo "built $out/libopus.so (opus $version)"
