#!/bin/sh
#
# ripcord-ps3 - cross-build openh264's H.264 DECODER for the PS3's PPE, and nothing else.
#
# WHY THIS EXISTS. DECODE.md section 1 settles the decoder base on openh264: CABAC is the one part of
# H.264 that is both large and unforgiving, it has to be bit-exact or the stream desynchronises
# silently, and there is no partial credit. Everything else the capture showed - Main not High,
# progressive, I and P only - would make a from-scratch decoder plausible, but it would spend the effort
# precisely where the risk is concentrated.
#
# WHAT IT DOES NOT DO: vendor openh264 into this repository. Fetched from a pinned, hash-verified
# upstream release at build time, into a gitignored directory - the same reasoning that keeps the interop
# constants generated rather than copied, and the same shape as ports/common/tools/build-mbedtls.sh. A
# checked-in 23,000-line third-party codec would be a maintenance and provenance liability nobody asked
# for.
#
# NOTHING IN OPENH264 IS PATCHED. Two things it expects are missing on a bare newlib target, and both are
# handled from outside: <stdio.h> is force-included because openh264 only pulls it in on Windows paths
# while using vsnprintf on all of them, and <sys/sysctl.h> is supplied by tools/shim - see that header
# for why a call that always fails is the correct answer here rather than a workaround.
#
# ONE FILE IS NOT BUILT: WelsThread.cpp, whose CWelsThread constructs a pte_handle_t from an int that
# PSL1GHT's pthreads-embedded does not offer. Nothing the decoder needs refers to it. Everything else
# builds, including the mutex and event primitives the decoder really does call, and PSL1GHT's libpthread
# supplies what those rest on. ProcessorCount resolves to 1 through openh264's own fallback path, which
# is the right answer: this port's parallelism is SPEs, which openh264 knows nothing about.
#
#     PS3DEV=... PSL1GHT=... ./build-openh264.sh [output-dir]
#
# Produces libopenh264dec.a plus the include paths a caller needs, and prints them.
#
# [X] NOT YET RUN ON A CONSOLE. The library builds and links for the PPE, and openh264 decodes this
# project's capture bit-exactly on big-endian PowerPC64 under qemu (DECODE.md section 1). Whether it
# decodes on the PPE itself, within the PS3's memory, is the next thing to establish.

set -eu

VERSION=2.6.0
SHA256=558544ad358283a7ab2930d69a9ceddf913f4a51ee9bf1bfb9e377322af81a69
URL="https://codeload.github.com/cisco/openh264/tar.gz/refs/tags/v${VERSION}"

: "${PS3DEV:?set PS3DEV - see ports/ripcord-ps3/SETUP.md}"
: "${PSL1GHT:?set PSL1GHT - it is the same directory as PS3DEV}"

HERE=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
OUT=${1:-"$HERE/../third-party/openh264"}
mkdir -p "$OUT"
OUT=$(CDPATH= cd -- "$OUT" && pwd)

PREFIX="$PS3DEV/ppu/bin/powerpc64-ps3-elf-"
CXX="${PREFIX}g++"
CC="${PREFIX}gcc"
AR="${PREFIX}ar"

TARBALL="$OUT/openh264-${VERSION}.tar.gz"
SRC="$OUT/openh264-${VERSION}"

if [ ! -d "$SRC" ]; then
    [ -f "$TARBALL" ] || curl -sSL -o "$TARBALL" "$URL"
    echo "$SHA256  $TARBALL" | sha256sum -c - >/dev/null
    tar xzf "$TARBALL" -C "$OUT"
fi

INC="-I$SRC/codec/decoder/core/inc -I$SRC/codec/decoder/plus/inc -I$SRC/codec/common/inc \
     -I$SRC/codec/api/wels -I$SRC/codec/api -I$HERE/shim -I$PSL1GHT/ppu/include"

# -O2 and nothing clever. The inner loops are going to be rewritten for the SPU's 128-bit SIMD anyway -
# that is the whole shape of the work in DECODE.md section 1 - so tuning the PPE build is effort spent on
# code that is scheduled to be replaced.
# -std=gnu++11, NOT -std=c++11, and this is the same trap the port's C code hit with -std=c99. Strict ISO
# mode defines __STRICT_ANSI__, under which newlib hides everything that is not ISO C++ - including
# vsnprintf, which openh264 calls on every platform while only including <stdio.h> on the Windows paths.
# The error names vsnprintf and suggests vsprintf, which reads like a missing include and is not one:
# force-including <stdio.h> changes nothing while the declaration is hidden behind the feature test.
FLAGS="-O2 -std=gnu++11 -include stdio.h $INC"

OBJ="$OUT/obj"
mkdir -p "$OBJ"
rm -f "$OBJ"/*.o

for f in "$SRC"/codec/decoder/core/src/*.cpp \
         "$SRC"/codec/decoder/plus/src/*.cpp \
         "$SRC"/codec/common/src/*.cpp; do
    b=$(basename "$f" .cpp)
    # The one file that genuinely cannot build here. CWelsThread constructs a pte_handle_t from an int,
    # and PSL1GHT's pthreads-embedded defines that as a struct with no such constructor. Nothing the
    # decoder needs refers to it - the class is openh264's worker-thread wrapper, and this port's
    # parallelism is SPEs. WelsThreadLib.cpp, which DOES provide the mutex and event primitives the
    # decoder calls, builds fine once tools/shim supplies <sys/sysctl.h>.
    case "$b" in WelsThread) continue ;; esac
    $CXX -c $FLAGS -o "$OBJ/$b.o" "$f"
done

$CC -c -O2 -I"$HERE/shim" -o "$OBJ/rc_sysctl_stub.o" "$HERE/shim/rc_sysctl_stub.c"

rm -f "$OUT/libopenh264dec.a"
$AR crs "$OUT/libopenh264dec.a" "$OBJ"/*.o

cat <<EOF
openh264 ${VERSION} decoder built for the PPE.

  library   $OUT/libopenh264dec.a
  includes  -I$SRC/codec/api/wels -I$SRC/codec/api
  link      -lpthread   (PSL1GHT's, for openh264's mutexes and condition variables)
EOF
