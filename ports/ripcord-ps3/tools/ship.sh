#!/bin/sh
#
# Build, verify, and only then upload.
#
# WHY THIS EXISTS. `make pkg` leaves the PREVIOUS package in place when it fails, and an upload does not
# care. Three builds in a row reported a new build number, uploaded cleanly, installed cleanly and ran
# the old binary - because the failure was "No rule to make target", which does not contain the word
# "error", and the check was a grep for that word.
#
# The lesson is not "grep for more words". It is that a build's success is its EXIT STATUS, and that the
# thing uploaded has to be shown to contain the build that was just made. Both are checked here.
set -e

cd "$(dirname "$0")/.."

: "${PS3DEV:=$HOME/ps3dev}"
: "${PSL1GHT:=$PS3DEV}"
export PS3DEV PSL1GHT
export PATH="$PS3DEV/bin:$PS3DEV/ppu/bin:$PS3DEV/spu/bin:$PATH"

HOST="${1:?usage: ship.sh <console-address>}"
PKG=ripcord-ps3.gnpdrm.pkg

make pkg                         # set -e: a non-zero exit stops here, before anything is uploaded

# THE PACKAGE STILL HAS TO PROVE IT CARRIES THE BUILD THAT WAS JUST MADE - see the note above. What
# changed is where it says so: the XMB title is now the program's name, and the build number is its
# VERSION, which is an NN.NN string whose digits ARE the build. So the check reads the version instead
# of the title, and the failure it exists to catch is unchanged.
# [a-z]* tolerates the BUILD_ORIGIN suffix - "b35ci" as well as "b460". Without it the pattern needs a
# quote straight after the digits, yields nothing for a CI build, and this script refuses to upload while
# blaming a stale package. The digits are what the SFO version can be compared against; the suffix says
# where the build came from and is deliberately not part of the comparison.
BUILD_ID=$(sed -n 's/.*"b\([0-9]*\)[a-z]*".*/\1/p' build/rc_build_id.h)
SFO_VER=$(strings build/pkg/PARAM.SFO | grep -oE '^[0-9][0-9]\.[0-9][0-9]$' | head -1)
SFO_BUILD=$(printf '%s' "${SFO_VER}" | tr -d '.' | sed 's/^0*//')

if [ -z "$BUILD_ID" ] || [ "$BUILD_ID" != "$SFO_BUILD" ]; then
    echo "REFUSING TO UPLOAD: compiled b$BUILD_ID but the package's version says ${SFO_VER:-nothing}." >&2
    echo "The package is stale - the XMB would show a build that is not in it." >&2
    exit 1
fi
BUILD_ID="b$BUILD_ID"

if [ "$PKG" -ot build/rc_build_id.h ]; then
    echo "REFUSING TO UPLOAD: $PKG is older than the build id it claims to carry." >&2
    exit 1
fi

echo "$BUILD_ID: package agrees, uploading to $HOST"
curl -sS -T "$PKG" "ftp://$HOST/dev_hdd0/packages/$PKG"
echo "$BUILD_ID uploaded - install it from the XMB; it is called Ripcord and its version is $SFO_VER"
