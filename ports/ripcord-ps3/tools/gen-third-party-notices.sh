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

# The PSL1GHT revision the linked binary was actually built from, as " (f649a08)", or nothing at all.
#
# The archive carries no licence file but it DOES carry build.txt, which names the exact ps3dev, PSL1GHT
# and ps3libraries commits it was built from - so the notice can say which PSL1GHT it describes rather
# than leaving a reader to assume. That is the part of "the licence of the version actually linked" that
# is recoverable here, and it is worth recovering: the MIT text has not changed since 2011, but a notice
# that names its subject can be checked and one that does not cannot.
#
# Degrades to an unlabelled heading rather than failing. A missing build.txt makes the notice less
# precise; it does not make the licence any less required, and refusing to emit it would invert the whole
# point of this change.
psl1ght_version() {
    [ -f "$PS3DEV/build.txt" ] || return 0
    awk '$1 == "PSL1GHT" { printf " (%s)", substr($2, 1, 7); exit }' "$PS3DEV/build.txt" 2>/dev/null || true
}

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

    # PSL1GHT: the runtime this port links against, and the one toolchain component whose licence is
    # MANDATORY rather than a courtesy. MIT requires "the above copyright notice and this permission
    # notice" in all copies, and every .self and .pkg this port produces is a copy.
    #
    # EMITTED UNCONDITIONALLY, FROM TEXT HELD HERE, and that is the correction rather than a shortcut.
    # An earlier revision read it from candidate paths under $PS3DEV and said nothing when none matched -
    # which is what happened on every build, because the ps3dev prebuilt archive ships no licence file
    # for anything. A find across the whole 577 MB unpacked tree returns exactly one, belonging to a
    # PolarSSL this port does not link. So the loop was not unlucky; it could never have succeeded, and
    # it failed silently for as long as it existed.
    #
    # This is the same decision #7 made for FreeType's mandatory disclaimer and for the same reason: an
    # obligation that only ships when an optional file happens to be present is an obligation that does
    # not ship. It is a checked-in copy of somebody else's licence, which this project otherwise avoids -
    # but the argument against that is drift from the version actually linked, and there is no local copy
    # to drift from. The commit below closes that gap instead.
    #
    # Provenance: https://github.com/ps3dev/PSL1GHT/blob/master/LICENSE, checked 2026-09-18 and found
    # byte-identical at master and at the commit $PS3DEV/build.txt names for the pinned nightly - so the
    # text below is right for the revision this toolchain links, not merely right for the project.
    #
    # The digest is deliberately NOT quoted here. A 64-character hex run in a comment is prose to the
    # published-tree sweep, which cannot tell a licence digest from key material and should not try; the
    # build scripts get away with theirs because those live in code. Re-verify by diffing this heredoc
    # against that file rather than against a number somebody pasted, which is the stronger check anyway.
    section "PSL1GHT$(psl1ght_version) - MIT"
    cat <<'PSL1GHT_MIT'
Copyright (c) 2011 PSL1GHT Development Team

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
PSL1GHT_MIT
} > "$OUT"

echo "gen-third-party-notices: wrote $OUT ($(wc -l < "$OUT") lines)"
