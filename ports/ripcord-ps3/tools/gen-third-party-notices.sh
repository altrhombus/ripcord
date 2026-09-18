#!/bin/sh
#
# Assemble THIRD-PARTY-NOTICES.txt for the .pkg, from the licences the build already fetched.
#
# WHY GENERATE RATHER THAN COMMIT ONE. Same argument as tools/gen_constants.py: a checked-in copy of
# somebody else's licence is a second copy that drifts. These texts arrive with the tarballs this port
# fetches and hash-verifies, so the generated file is the licence of the exact version linked into the
# binary being packaged, not the licence of whatever version was current when a human last pasted it.
#
# WHY IT GOES IN THE PACKAGE AND NOT ONLY IN THE REPOSITORY. openh264 (BSD-2), Opus (BSD-3) and
# Mbed TLS (Apache-2.0) each require their notice to travel with a BINARY distribution - BSD's "in the
# documentation and/or other materials provided with the distribution", Apache-2.0 section 4's "give any
# other recipients a copy of this License". A .pkg carries EBOOT.BIN, ICON0.PNG and PARAM.SFO. It does
# not carry this repository's NOTICE file, so a NOTICE alone satisfies none of them for someone who
# downloads a package.
#
# HARD FAILURE VS BEST EFFORT, and the split is deliberate.
#
#   - The three fetched dependencies REQUIRE reproduction. If a licence file is missing, this script
#     exits non-zero and the package is not built. Shipping a binary with an incomplete notices file is
#     worse than not shipping one.
#   - FreeType's mandatory obligation for binary form is a fixed DISCLAIMER, not the licence text, so
#     that sentence is emitted unconditionally and cannot go missing. FTL.TXT is included as well when
#     the toolchain has it.
#   - zlib's licence asks nothing of a binary distribution. Included when found, as a courtesy.
set -e

OUT="${1:?usage: gen-third-party-notices.sh <output file>}"
TP="$(dirname "$0")/../third-party"
: "${PS3DEV:=$HOME/ps3dev}"
: "${PSL1GHT:=$PS3DEV}"

require() {
    # $1 = display name, $2 = the fetch directory (empty if the fetch never ran), $3 = licence file
    # inside it, $4 = what to do about it. The directory is checked separately from the file so the
    # error says "never fetched" rather than pointing at a nonsensical path like /LICENSE, which is
    # what an unset directory variable produces and what the first version of this reported.
    if [ -z "$2" ]; then
        echo "gen-third-party-notices: $1 has not been fetched - no directory under $TP" >&2
        echo "  $4" >&2
        exit 1
    fi
    if [ ! -f "$2/$3" ]; then
        echo "gen-third-party-notices: $1 was fetched but carries no $3 at $2/$3" >&2
        echo "  the tarball layout changed; this script needs updating rather than re-running" >&2
        exit 1
    fi
}

# Versions come from the directory the fetch produced, so they cannot disagree with what was linked.
OPENH264_DIR=$(ls -d "$TP"/openh264/openh264-* 2>/dev/null | head -1)
OPUS_DIR=$(ls -d "$TP"/opus/opus-* 2>/dev/null | head -1)
MBEDTLS_DIR=$(ls -d "$TP"/mbedtls/src/mbedtls-* 2>/dev/null | head -1)

require "openh264" "$OPENH264_DIR" "LICENSE" "run tools/build-openh264.sh first"
require "Opus"     "$OPUS_DIR"     "COPYING" "run tools/build-opus.sh first"
require "Mbed TLS" "$MBEDTLS_DIR"  "LICENSE" "run ../common/tools/build-mbedtls.sh first"

section() { printf '\n\n%s\n%s\n\n' "$1" "$(echo "$1" | tr '[:print:]' '-')"; }

{
    cat <<'HEADER'
THIRD-PARTY NOTICES - ripcord-ps3
=================================

This package links the components below statically. Each is governed by its own
licence, reproduced here in full where that licence asks for it.

Ripcord itself is Apache-2.0; see LICENSE and NOTICE in the source repository at
https://github.com/altrhombus/ripcord

This file is generated at package time from the licence texts shipped with the
exact versions linked into this binary. It is not maintained by hand.
HEADER

    section "openh264 $(basename "$OPENH264_DIR" | sed 's/^openh264-//') - BSD-2-Clause"
    cat "$OPENH264_DIR/LICENSE"

    section "Opus $(basename "$OPUS_DIR" | sed 's/^opus-//') - BSD-3-Clause"
    cat "$OPUS_DIR/COPYING"

    section "Mbed TLS $(basename "$MBEDTLS_DIR" | sed 's/^mbedtls-//') - Apache-2.0"
    cat "$MBEDTLS_DIR/LICENSE"

    # FreeType: the disclaimer below is what the FTL requires of a BINARY distribution (FTL section 1).
    # It is fixed text and is emitted whether or not the toolchain's copy of FTL.TXT can be found, so
    # the mandatory part of the obligation cannot be lost to a missing file.
    section "FreeType - FreeType License (FTL)"
    cat <<'FREETYPE'
Portions of this software are copyright (c) 2026 The FreeType Project
(https://freetype.org). All rights reserved.

This software is based in part on the work of the FreeType Team.

Ripcord elects the FreeType License, not the GPLv2 alternative the FreeType
Project also offers. FreeType is supplied by the ps3dev toolchain's portlibs
rather than fetched by this repository.
FREETYPE
    for f in "$PS3DEV/portlibs/ppu/share/doc/freetype/FTL.TXT" \
             "$PS3DEV/portlibs/ppu/share/licenses/freetype/FTL.TXT" \
             "$PS3DEV/licenses/freetype/FTL.TXT"; do
        [ -f "$f" ] && { printf '\n'; cat "$f"; break; }
    done

    # zlib asks nothing of a binary distribution. Included when present, as a courtesy.
    for f in "$PS3DEV/portlibs/ppu/share/doc/zlib/LICENSE" \
             "$PS3DEV/portlibs/ppu/share/licenses/zlib/LICENSE"; do
        [ -f "$f" ] && { section "zlib"; cat "$f"; break; }
    done

    # PSL1GHT: the runtime this port links against. Included when the toolchain carries a licence file.
    for f in "$PSL1GHT/LICENSE" "$PSL1GHT/COPYING" "$PSL1GHT/licence.txt"; do
        [ -f "$f" ] && { section "PSL1GHT"; cat "$f"; break; }
    done
} > "$OUT"

echo "gen-third-party-notices: wrote $OUT ($(wc -l < "$OUT") lines)"
