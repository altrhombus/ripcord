#!/usr/bin/env python3
"""Commit to the private evidence folder in a manifest that reveals nothing about it.

The captures folder (docs/protocol/captures/) holds the captures, dumps, lab notebook and analysis
scripts the protocol was derived from, and it is never committed. This script writes a manifest of
keyed hashes, and nothing else, to docs/protocol/evidence-manifest.tsv so the repository can carry a
dated record of the evidence without carrying the evidence:

    <HMAC-SHA256(key, relative path)>  <HMAC-SHA256(key, content)>

Both are keyed with a random 32-byte key kept beside the evidence (docs/protocol/captures/
evidence-manifest.key, created on first run, never committed). Without the key the manifest can't be
used to confirm a guessed file name or a known file's content; with it, anyone can check every row.
Publishing the key is how the manifest is proven later. Sizes stay in the private path map
(docs/protocol/captures/evidence-manifest-paths.tsv), since an exact size identifies a known file too.

What a manifest commit proves: that each listed file existed, in exactly that form, by the commit's
date. What it cannot prove: anything about dates before its first commit. The ordering record for the
derivation stays the research log and the lab notebook.

Usage:
    python tools/evidence-manifest.py            # regenerate the manifest and the private path map
    python tools/evidence-manifest.py --check    # compare the folder against the committed manifest

Exclusions: tooling and working files that are not evidence (listed in DEFAULT_EXCLUDES), plus any
patterns in docs/protocol/captures/evidence-manifest.exclude, one per line, matched against the
relative path with fnmatch; that file is private too.
"""
from __future__ import annotations

import fnmatch
import hashlib
import hmac
import os
import secrets
import sys
from datetime import datetime, timezone

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EVIDENCE = os.path.join(REPO, "docs", "protocol", "captures")
MANIFEST = os.path.join(REPO, "docs", "protocol", "evidence-manifest.tsv")
PATH_MAP = os.path.join(EVIDENCE, "evidence-manifest-paths.tsv")
KEY_FILE = os.path.join(EVIDENCE, "evidence-manifest.key")
EXCLUDE_FILE = os.path.join(EVIDENCE, "evidence-manifest.exclude")

DEFAULT_EXCLUDES = [
    ".venv/*",
    "*/__pycache__/*",
    "__pycache__/*",
    "*.pyc",
    "HANDOFF.md",
    "leak-denylist.tsv",
    "evidence-manifest-paths.tsv",
    "evidence-manifest.exclude",
    "evidence-manifest.key",
]


def key() -> bytes:
    """The manifest key, created on first use. Losing it makes the committed manifest unverifiable."""
    if not os.path.exists(KEY_FILE):
        with open(KEY_FILE, "w", encoding="utf-8", newline="\n") as f:
            f.write(secrets.token_hex(32) + "\n")
    with open(KEY_FILE, encoding="utf-8") as f:
        return bytes.fromhex(f.read().strip())


def excludes() -> list[str]:
    patterns = list(DEFAULT_EXCLUDES)
    if os.path.exists(EXCLUDE_FILE):
        with open(EXCLUDE_FILE, encoding="utf-8") as f:
            for line in f:
                line = line.strip()
                if line and not line.startswith("#"):
                    patterns.append(line)
    return patterns


def excluded(rel: str, patterns: list[str]) -> bool:
    return any(fnmatch.fnmatch(rel, p) for p in patterns)


def hmac_file(k: bytes, path: str) -> str:
    h = hmac.new(k, digestmod=hashlib.sha256)
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def walk() -> list[str]:
    patterns = excludes()
    out = []
    for dirpath, dirnames, filenames in os.walk(EVIDENCE):
        rel_dir = os.path.relpath(dirpath, EVIDENCE).replace(os.sep, "/")
        rel_dir = "" if rel_dir == "." else rel_dir + "/"
        # Prune excluded directories so a virtual environment is never even walked.
        dirnames[:] = [d for d in dirnames if not excluded(rel_dir + d + "/x", patterns)]
        for name in filenames:
            rel = rel_dir + name
            if not excluded(rel, patterns):
                out.append(rel)
    return sorted(out)


def hash_all() -> list[tuple[str, str, int, str]]:
    k = key()
    rows = []
    for rel in walk():
        full = os.path.join(EVIDENCE, rel)
        path_hash = hmac.new(k, rel.encode("utf-8"), hashlib.sha256).hexdigest()
        rows.append((path_hash, rel, os.path.getsize(full), hmac_file(k, full)))
    rows.sort(key=lambda r: r[0])
    return rows


def write(rows: list[tuple[str, str, int, str]]) -> None:
    today = datetime.now(timezone.utc).date().isoformat()
    with open(MANIFEST, "w", encoding="utf-8", newline="\n") as f:
        f.write("# Evidence manifest: keyed hashes of the private captures folder, nothing else. See tools/evidence-manifest.py.\n")
        f.write("# With the key, a commit of it shows the listed files existed in this form by the commit date; it proves nothing earlier.\n")
        f.write(f"# Generated {today}: {len(rows)} files.\n")
        f.write("# path_hmac\tcontent_hmac\n")
        for path_hash, _rel, _size, content_hash in rows:
            f.write(f"{path_hash}\t{content_hash}\n")
    with open(PATH_MAP, "w", encoding="utf-8", newline="\n") as f:
        f.write("# Private: the size and path behind each path_hmac in docs/protocol/evidence-manifest.tsv. Never committed.\n")
        for path_hash, rel, size, _content in rows:
            f.write(f"{path_hash}\t{size}\t{rel}\n")


def read_manifest() -> dict[str, str]:
    out = {}
    with open(MANIFEST, encoding="utf-8") as f:
        for line in f:
            if line.startswith("#") or not line.strip():
                continue
            path_hash, content_hash = line.rstrip("\n").split("\t")
            out[path_hash] = content_hash
    return out


def check() -> int:
    committed = read_manifest()
    rows = hash_all()
    seen = set()
    changed = new = 0
    for path_hash, rel, size, content_hash in rows:
        seen.add(path_hash)
        if path_hash not in committed:
            new += 1
            print(f"new      {rel}")
        elif committed[path_hash] != content_hash:
            changed += 1
            print(f"changed  {rel}")
    missing = len(set(committed) - seen)
    print(f"{len(rows)} files: {changed} changed, {new} new, {missing} missing from the folder")
    return 1 if (changed or missing) else 0


def main() -> int:
    if not os.path.isdir(EVIDENCE):
        print("no captures folder on this machine; nothing to hash", file=sys.stderr)
        return 1
    if "--check" in sys.argv[1:]:
        return check()
    rows = hash_all()
    write(rows)
    print(f"wrote {os.path.relpath(MANIFEST, REPO)} ({len(rows)} files) and the private path map")
    return 0


if __name__ == "__main__":
    sys.exit(main())
