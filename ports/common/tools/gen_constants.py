#!/usr/bin/env python3
"""Generate halyard_v1_constants.g.c from the committed interoperability-constants bundle.

WHY GENERATE RATHER THAN COPY.  The bundle
(src/Ripcord.Protocol.Halyard/Data/halyard-v1-constants.json) is committed under a specific, narrow
argument in the repository NOTICE: these are values the console computes against, identical for every user
and every console, and a client cannot speak the protocol without them.  That argument is made once, about
one file.  A second checked-in copy of the same bytes in this port would quietly turn one bounded exception
into two, and the second would carry no argument at all.  So this port keeps no copy: the build reads the
one committed file and generates a C translation unit into the build directory, which is gitignored.

It also means the port cannot drift.  If the bundle is ever corrected, ripcord-3ds picks up the correction on
the next build rather than on the next time somebody remembers.

Parsing JSON on an ARM11 at 268 MHz to reach a lookup table would also be silly, which is the other half of
the reason this is a build step and not runtime code.

Only the CONTROL-plane constants are emitted.  The registration tables in the same bundle drive PIN
pairing, which this port deliberately does not do -- ripcord-3ds consumes a pairing record produced by a
desktop Ripcord install (see the README), so the registration path never runs on the handheld and its
constants have no business being in the binary.

Usage:  gen_constants.py <bundle.json> <output.c>
"""

import json
import sys

KDF_TABLE_LENGTH = 512
CONTEXT_KEY_LENGTH = 16


def unhex(name, value, expected_length=None):
    if value is None:
        return None
    try:
        raw = bytes.fromhex(value)
    except ValueError as exc:
        raise SystemExit(f"{name}: not valid hex ({exc})")
    if expected_length is not None and len(raw) != expected_length:
        raise SystemExit(f"{name}: expected {expected_length} bytes, got {len(raw)}")
    return raw


def emit_array(name, data, length):
    """Emit a byte array, or an all-zero placeholder of the right size when absent.

    An absent table is represented as zeros plus a flag rather than an omitted symbol, so the C side links
    either way and fails at the one place that checks the flag -- instead of failing at link time with an
    error that says nothing about interop constants.
    """
    if data is None:
        data = bytes(length)
    lines = [f"const uint8_t {name}[{length}] = {{"]
    for offset in range(0, len(data), 12):
        chunk = data[offset:offset + 12]
        lines.append("    " + " ".join(f"0x{b:02x}," for b in chunk))
    lines.append("};")
    return "\n".join(lines)


def main(argv):
    if len(argv) != 3:
        raise SystemExit("usage: gen_constants.py <bundle.json> <output.c>")

    bundle_path, output_path = argv[1], argv[2]

    try:
        with open(bundle_path, "r", encoding="utf-8") as handle:
            bundle = json.load(handle)
    except FileNotFoundError:
        raise SystemExit(f"bundle not found: {bundle_path}")
    except json.JSONDecodeError as exc:
        raise SystemExit(f"bundle is not valid JSON: {exc}")

    kdf1 = unhex("kdfTable1", bundle.get("kdfTable1"), KDF_TABLE_LENGTH)
    kdf2 = unhex("kdfTable2", bundle.get("kdfTable2"), KDF_TABLE_LENGTH)
    ps4_kdf1 = unhex("ps4KdfTable1", bundle.get("ps4KdfTable1"), KDF_TABLE_LENGTH)
    ps4_kdf2 = unhex("ps4KdfTable2", bundle.get("ps4KdfTable2"), KDF_TABLE_LENGTH)

    context_keys = bundle.get("contextKeys") or {}
    codec_in_high = unhex("codecInHigh", context_keys.get("codecInHigh"), CONTEXT_KEY_LENGTH)
    selector_one = unhex("selectorOne", context_keys.get("selectorOne"), CONTEXT_KEY_LENGTH)
    selector_zero = unhex("selectorZero", context_keys.get("selectorZero"), CONTEXT_KEY_LENGTH)
    fallback_zero = unhex("fallbackZero", context_keys.get("fallbackZero"), CONTEXT_KEY_LENGTH)

    # The PS5 pair and all four context keys are the minimum for a PS5 session.  Anything less and the
    # session would silently fall back to no encryption on the .NET side; here it must be a hard, named
    # failure at build time, because a handheld has nowhere good to report it at runtime.
    control_complete = all(
        value is not None
        for value in (kdf1, kdf2, codec_in_high, selector_one, selector_zero, fallback_zero)
    )
    has_ps4 = ps4_kdf1 is not None and ps4_kdf2 is not None

    parts = [
        "/*",
        " * GENERATED FILE - DO NOT EDIT, DO NOT COMMIT.",
        " *",
        f" * Produced by ports/ripcord-3ds/tools/gen_constants.py from {bundle_path}.",
        " * Regenerate with `make constants` (the normal build does it for you).",
        " *",
        " * These are the generic v1 interoperability constants described in the repository NOTICE:",
        " * identical for every console and every account, and required to speak the protocol at all.",
        " * No per-console, per-account or per-session material appears here or anywhere in this port.",
        " */",
        '#include "halyard_v1.h"',
        "",
        f"const int halyard_v1_constants_bundled = {1 if control_complete else 0};",
        f"const int halyard_v1_has_ps4_tables = {1 if has_ps4 else 0};",
        "",
        emit_array("halyard_v1_kdf_table1", kdf1, KDF_TABLE_LENGTH),
        "",
        emit_array("halyard_v1_kdf_table2", kdf2, KDF_TABLE_LENGTH),
        "",
        emit_array("halyard_v1_ps4_kdf_table1", ps4_kdf1, KDF_TABLE_LENGTH),
        "",
        emit_array("halyard_v1_ps4_kdf_table2", ps4_kdf2, KDF_TABLE_LENGTH),
        "",
        emit_array("halyard_v1_ctx_codec_in_high", codec_in_high, CONTEXT_KEY_LENGTH),
        "",
        emit_array("halyard_v1_ctx_selector_one", selector_one, CONTEXT_KEY_LENGTH),
        "",
        emit_array("halyard_v1_ctx_selector_zero", selector_zero, CONTEXT_KEY_LENGTH),
        "",
        emit_array("halyard_v1_ctx_fallback_zero", fallback_zero, CONTEXT_KEY_LENGTH),
        "",
    ]

    with open(output_path, "w", encoding="utf-8") as handle:
        handle.write("\n".join(parts))

    status = "complete" if control_complete else "INCOMPLETE (control constants missing)"
    ps4_status = "with PS4 tables" if has_ps4 else "PS5 only"
    print(f"gen_constants: wrote {output_path} - {status}, {ps4_status}")

    if not control_complete:
        raise SystemExit(
            "gen_constants: the bundle is missing control-plane constants. A build without them cannot "
            "encrypt the control session; refusing to produce one silently."
        )

    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
