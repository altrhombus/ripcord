#!/bin/sh
#
# ripcord-ps3 - cross-build libopus for the PS3's PPE.
#
# WHY THIS EXISTS. The console's audio is Opus: 48 kHz stereo in 10 ms frames, the same as every other
# Ripcord front end sees (src/Ripcord.Media.Audio/OpusAudioDecoder.cs fixes the rate, and ripcord-3ds
# reaches the same numbers from libopus). ps3dev's portlibs carry libogg, FLAC and two SDL mixers, and
# no Opus - so it has to be built, the way openh264 is.
#
# SAME SHAPE AS tools/build-openh264.sh, deliberately: fetched from a pinned, hash-verified upstream
# release at build time into a gitignored directory, never vendored. A checked-in third-party codec is a
# maintenance and provenance liability, and the reasoning that keeps the interop constants generated
# rather than copied applies here too.
#
# LICENCE. Opus is BSD-3-Clause, which is the permissive-only line this port's dependencies have held
# (mbedtls Apache-2.0, openh264 BSD-2-Clause). DECODE.md section 1 sets out why that line matters and why
# an LGPL decoder was recommended against; nothing here complicates it.
#
# FLOAT, NOT FIXED POINT. The PPE has a real FPU and this is not the 3DS; opus's float path is the
# better-tested one and the default. The 1.5 series' neural extensions (DRED, deep PLC, OSCE) are
# switched off - they are large, they are for lossy links that need reconstruction, and this is a LAN.
#
#     PS3DEV=... PSL1GHT=... ./build-opus.sh [output-dir]
#
# Produces libopus.a and prints the include path a caller needs.

set -eu

VERSION=1.5.2
SHA256=65c1d2f78b9f2fb20082c38cbe47c951ad5839345876e46941612ee87f9a7ce1
URL="https://downloads.xiph.org/releases/opus/opus-${VERSION}.tar.gz"

: "${PS3DEV:?set PS3DEV - see ports/ripcord-ps3/SETUP.md}"
: "${PSL1GHT:?set PSL1GHT - it is the same directory as PS3DEV}"

HERE=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
OUT=${1:-"$HERE/../third-party/opus"}
mkdir -p "$OUT"
OUT=$(CDPATH= cd -- "$OUT" && pwd)

PREFIX="$PS3DEV/ppu/bin/powerpc64-ps3-elf-"

TARBALL="$OUT/opus-${VERSION}.tar.gz"
SRC="$OUT/opus-${VERSION}"

if [ ! -d "$SRC" ]; then
    [ -f "$TARBALL" ] || curl -sSL -o "$TARBALL" "$URL"
    echo "$SHA256  $TARBALL" | sha256sum -c - >/dev/null
    tar xzf "$TARBALL" -C "$OUT"
fi

BUILD="$OUT/build"
mkdir -p "$BUILD"

cd "$BUILD"
if [ ! -f Makefile ]; then
    # --host without --build is enough for autotools to know this is a cross-compile; the rest is
    # switching off everything that is not the decoder we need.
    #
    # -mcpu=cell and -mhard-float match the port's own flags. The Makefile's comment on why those come
    # from the SDK rather than from the hardware applies to every object that will be linked together.
    "$SRC/configure" \
        --host=powerpc64-ps3-elf \
        --prefix="$OUT" \
        --disable-shared --enable-static \
        --disable-doc --disable-extra-programs \
        --disable-stack-protector \
        --disable-deep-plc --disable-dred --disable-osce \
        CC="${PREFIX}gcc" AR="${PREFIX}ar" RANLIB="${PREFIX}ranlib" \
        CFLAGS="-O2 -mcpu=cell -mhard-float" \
        >/dev/null
fi

make -j"$(nproc 2>/dev/null || echo 2)" >/dev/null
make install >/dev/null

echo "libopus:  $OUT/lib/libopus.a"
echo "include:  $OUT/include/opus"
