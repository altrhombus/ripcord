#!/usr/bin/env python3
"""Populate the bundled OAuth client credential from the authors' own sign-in capture.

Provenance, because it is the whole point of this script existing rather than the value simply
being typed in: the credential is read out of *our own* capture of *our own* traffic
(docs/protocol/captures/, the gitignored dirty room), which is the same source every other
interoperability fact in this project comes from. It is not copied from any other implementation.
That it also appears in other projects was established afterwards, by looking, and is a fact about
the world rather than our source -- see NOTICE.

Run from the repository root:

    python3 tools/extract-oauth-client.py

The capture is not distributed with the repository, so this only works on a machine that has the
dirty room. Note that the committed file already ships populated -- this regenerates the value, it
does not supply one a clone lacks. An empty file is inert, but that is not the shipped state.
"""

import base64
import json
import pathlib
import sys
import zipfile

CAPTURE = pathlib.Path("docs/protocol/captures/session6-new-sign-in-with-remoteplay.saz")
TARGET = pathlib.Path("src/Ripcord.Cloud.Halyard/Data/halyard-oauth-client.json")

# The authorization-code exchange. The refresh grant (raw/117_c.txt) carries the identical
# credential, so either would do; this one is simply the first.
ENTRY = "raw/096_c.txt"


def main() -> int:
    if not CAPTURE.exists():
        print(f"capture not found: {CAPTURE}", file=sys.stderr)
        print("This machine has no dirty room; nothing to do.", file=sys.stderr)
        return 1

    with zipfile.ZipFile(CAPTURE) as archive:
        request = archive.read(ENTRY).decode("utf8", "replace")

    credential = None
    for line in request.split("\r\n"):
        if line.lower().startswith("authorization:"):
            _, _, value = line.partition(" ")
            scheme, _, encoded = value.strip().partition(" ")
            if scheme.lower() == "basic":
                credential = base64.b64decode(encoded).decode()
                break

    if credential is None:
        print(f"no Basic credential in {ENTRY}", file=sys.stderr)
        return 1

    client_id, separator, client_secret = credential.partition(":")
    if not separator or not client_id or not client_secret:
        print("credential did not split into id and secret", file=sys.stderr)
        return 1

    document = json.loads(TARGET.read_text())
    document["clientId"] = client_id
    document["clientSecret"] = client_secret
    TARGET.write_text(json.dumps(document, indent=2) + "\n", newline="\n")

    # Shape only. Printing the credential would put it in a terminal scrollback and any shell
    # history or CI log that captures stdout, for no benefit -- it is going into a file that is
    # about to be committed anyway.
    print(f"wrote {TARGET}")
    print(f"  clientId:     {len(client_id)} chars")
    print(f"  clientSecret: {len(client_secret)} chars")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
