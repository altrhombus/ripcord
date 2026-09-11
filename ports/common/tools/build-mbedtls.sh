#!/bin/sh
#
# ripcord ports - cross-build the Mbed TLS ECP/MPI layer, and nothing else.
#
# WHY THIS EXISTS. ports/common/crypto/rc_ecdh.c delegates one primitive - ECDH over P-256/P-521 - to
# Mbed TLS, for the reason rc_ecdh.h gives at length: a hand-written constant-time bigint is the last
# thing this project should write twice. devkitPro packages that library for the 3DS as `3ds-mbedtls`.
# vitasdk packages no equivalent (its vdpm list has openssl and libsodium; libsodium is
# Curve25519/Ed25519 and cannot do the NIST curves at all), so this port builds its own.
#
# WHAT IT DOES NOT DO: vendor Mbed TLS into this repository. The source is fetched from a pinned,
# hash-verified upstream release at build time and the result lands in a gitignored directory - the same
# reasoning that keeps the interop constants generated rather than copied. A checked-in third-party
# crypto tree would be a maintenance and provenance liability nobody asked for.
#
# VERSION IS PINNED TO MATCH THE 3DS. devkitPro ships 2.28.8; building anything else here would mean two
# ports running different curve code behind one seam, and 3.x moved struct fields behind accessors, so
# rc_ecdh.c would need a second code path. One version, one code path.
#
#     PREFIX=<dir> ./build-mbedtls.sh                       cross-build for the Vita (the default)
#     PREFIX=<dir> CROSS= ./build-mbedtls.sh                 build for THIS machine, for the host tests
#     PREFIX=<dir> CROSS=arm-none-eabi- ./build-mbedtls.sh   some other toolchain
#
# It lives in ports/common/tools rather than in one port because it serves ports/common/crypto/rc_ecdh.c,
# and two different builds already want it: the Vita port, which has no packaged mbedtls, and the host
# test suite, whose ecdh_test otherwise skips on any machine without libmbedtls-dev installed. The 3DS
# does not need it - devkitPro packages 3ds-mbedtls - which is why the version here is pinned to match
# that package rather than chosen freely.
#
# The Makefile of each caller runs it automatically; run it by hand only to re-fetch or to debug.
# Only the host suite is in this tree - the Vita port is on its own branch - so the CROSS= (empty)
# path below is the only one exercised here.

set -eu

VERSION=2.28.8
SHA256=4fef7de0d8d542510d726d643350acb3cdb9dc76ad45611b59c9aa08372b4213
URL="https://codeload.github.com/Mbed-TLS/mbedtls/tar.gz/refs/tags/v${VERSION}"

# The default names vitasdk's triple because the Vita port is the caller that needs a cross-build at
# all. Left pointing there rather than re-aimed at the host: it is correct for the caller it exists
# for, and the host suite passes CROSS= explicitly precisely so it does not depend on this default.
CROSS="${CROSS-arm-vita-eabi-}"   # CROSS= (empty) means the host compiler, not the default
TOOLS=$(cd "$(dirname "$0")" && pwd)
PREFIX="${PREFIX:?set PREFIX to where the library should be installed}"
WORK="$PREFIX/src"
CONFIG="$TOOLS/ripcord-mbedtls-config.h"

# Exactly the translation units the ECP/MPI layer needs. Established by compiling and linking
# rc_ecdh.c against them, not by reading the upstream Makefile: bignum (mbedtls_mpi_*), ecp +
# ecp_curves (mbedtls_ecp_*), platform_util (mbedtls_platform_zeroize) and constant_time (the
# side-channel-hardened helpers the other two call).
MODULES="bignum ecp ecp_curves platform_util constant_time"

if [ -f "$PREFIX/lib/libmbedcrypto.a" ] && [ "$PREFIX/lib/libmbedcrypto.a" -nt "$CONFIG" ]; then
    echo "mbedtls: up to date ($PREFIX/lib/libmbedcrypto.a)"
    exit 0
fi

command -v "${CROSS}gcc" >/dev/null 2>&1 || {
    echo "error: ${CROSS}gcc not on PATH - is VITASDK/bin exported?" >&2; exit 1; }

mkdir -p "$WORK" "$PREFIX/lib"

TARBALL="$WORK/mbedtls-${VERSION}.tar.gz"
if [ ! -f "$TARBALL" ]; then
    echo "mbedtls: fetching ${VERSION}"
    curl -fsSL -o "$TARBALL.part" "$URL"
    mv "$TARBALL.part" "$TARBALL"
fi

# Verify BEFORE extracting. This is a cryptography library being pulled over a network into a build
# that derives session keys; an unverified tarball would make the pin decorative.
echo "mbedtls: verifying"
ACTUAL=$(sha256sum "$TARBALL" | cut -d' ' -f1)
if [ "$ACTUAL" != "$SHA256" ]; then
    echo "error: sha256 mismatch for $TARBALL" >&2
    echo "  expected $SHA256" >&2
    echo "  actual   $ACTUAL" >&2
    echo "  refusing to build. Delete the file to re-fetch, or check why upstream changed." >&2
    exit 1
fi

SRC="$WORK/mbedtls-${VERSION}"
[ -d "$SRC" ] || tar xzf "$TARBALL" -C "$WORK"

echo "mbedtls: building $MODULES for ${CROSS%-}"
mkdir -p "$WORK/obj"
CFLAGS="-O2 -std=c99 -Wall -ffunction-sections -fdata-sections"
CFLAGS="$CFLAGS -I$SRC/include -I$TOOLS -DMBEDTLS_CONFIG_FILE=<ripcord-mbedtls-config.h>"
OBJS=""
for m in $MODULES; do
    "${CROSS}gcc" $CFLAGS -c "$SRC/library/$m.c" -o "$WORK/obj/$m.o"
    OBJS="$OBJS $WORK/obj/$m.o"
done

rm -f "$PREFIX/lib/libmbedcrypto.a"
"${CROSS}ar" rcs "$PREFIX/lib/libmbedcrypto.a" $OBJS

# Headers: rc_ecdh.c includes <mbedtls/ecp.h> and friends, so the tree has to be reachable. They are
# small, and copying them keeps the include path independent of where the source was unpacked.
rm -rf "$PREFIX/include"
mkdir -p "$PREFIX/include"
cp -r "$SRC/include/mbedtls" "$PREFIX/include/"
cp "$CONFIG" "$PREFIX/include/"

echo "mbedtls: $(${CROSS}size -t "$PREFIX/lib/libmbedcrypto.a" 2>/dev/null | tail -1 | awk '{print $1" bytes text"}')"
echo "mbedtls: installed to $PREFIX"
