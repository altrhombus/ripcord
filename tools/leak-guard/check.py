#!/usr/bin/env python3
"""The leak guard: refuse a commit or a push that carries a value from the captures folder.

Called by the hooks in .githooks (`git config core.hooksPath .githooks` turns them on):
  check.py --staged          pre-commit: the lines this commit adds
  check.py --message FILE    commit-msg: the commit message
  check.py --push REMOTE     pre-push: every outgoing commit's added lines and message (refs on stdin)
  check.py --history         every commit on every local branch, for an audit by hand

It reads docs/protocol/captures/leak-denylist.tsv, which build-denylist.py writes from the captures folder and which
is never committed. With no captures folder, as in any clone but the owner's, it does nothing. PublishedTreeSweepTests
is the check that needs no secrets and runs in CI; this is the one that knows the real values.

A match is reported by where it is and what kind of value it is, never by the value, so the report is safe to
paste anywhere. Addresses are recognised in every spelling the protocol uses (dotted, the Host header's padded
columns, byte arrays in decimal or hex) and hex values through any separators, so reformatting a value does
not hide it.
"""

import os
import re
import subprocess
import sys

DENYLIST = os.path.join("docs", "protocol", "captures", "leak-denylist.tsv")
ZERO = "0" * 40

DOTTED = re.compile(r"(?<![\d.])(\d{1,3})\. {0,2}(\d{1,3})\. {0,2}(\d{1,3})\. {0,2}(\d{1,3})(?![\d.])")
# Exactly four elements inside brackets, braces or parentheses: [172, 31, 0, 1], { 0xac, ... },
# Ipv4Addr::new(10, 0, 0, 1). Four numbers inside a longer argument list are data, not an address.
BYTES = re.compile(r"[\[{(]\s*(0x[0-9a-fA-F]{1,2}|\d{1,3})\s*,\s*(0x[0-9a-fA-F]{1,2}|\d{1,3})\s*,\s*"
                   r"(0x[0-9a-fA-F]{1,2}|\d{1,3})\s*,\s*(0x[0-9a-fA-F]{1,2}|\d{1,3})\s*[\]})]")
HEX_RUN = re.compile(r"[0-9a-fA-F](?:[ :,._-]?[0-9a-fA-F])*")
DIGITS = re.compile(r"(?<!\d)\d{19}(?!\d)")


class Denylist:
    def __init__(self, path):
        self.ipv4, self.account, self.words, self.hex = set(), set(), {}, {}
        with open(path, encoding="utf-8") as f:
            for line in f:
                if line.startswith("#") or "\t" not in line:
                    continue
                category, value = line.rstrip("\n").split("\t", 1)
                if category == "ipv4":
                    self.ipv4.add(value)
                elif category == "account-id":
                    self.account.add(value)
                elif category in ("hex", "mac"):
                    self.hex.setdefault(len(value), {})[value] = category
                else:
                    self.words[value.lower()] = category
        self.lengths = sorted(self.hex)
        self.word_rx = (re.compile(r"(?<![\w.-])(" + "|".join(re.escape(w) for w in sorted(self.words, key=len,
                                                                                            reverse=True))
                                   + r")(?![\w-])", re.IGNORECASE) if self.words else None)

    def scan(self, line):
        """The categories this line carries, deduplicated."""
        hits = set()
        for rx in (DOTTED, BYTES):
            for m in rx.finditer(line):
                try:
                    octets = [int(g, 16) if g.lower().startswith("0x") else int(g) for g in m.groups()]
                except ValueError:
                    continue
                if all(o <= 255 for o in octets) and ".".join(map(str, octets)) in self.ipv4:
                    hits.add("ipv4")
        for m in DIGITS.finditer(line):
            if m.group(0) in self.account:
                hits.add("account-id")
        if self.word_rx:
            for m in self.word_rx.finditer(line):
                hits.add(self.words[m.group(1).lower()])
        if self.lengths:
            for m in HEX_RUN.finditer(line):
                flat = re.sub(r"[ :,._-]", "", m.group(0)).lower()
                for n in self.lengths:
                    if n > len(flat):
                        break
                    table = self.hex[n]
                    for i in range(len(flat) - n + 1):
                        category = table.get(flat[i:i + n])
                        if category:
                            hits.add(category)
                            break
        return hits


def git(*args):
    return subprocess.run(["git", *args], capture_output=True, check=True).stdout.decode("utf-8", "replace")


def added_lines(patch):
    """(file, line number, text) for every added line of a unified diff."""
    path, number = "?", 0
    for raw in patch.split("\n"):
        if raw.startswith("+++ "):
            path = raw[6:] if raw.startswith("+++ b/") else raw[4:]
        elif raw.startswith("@@"):
            m = re.search(r"\+(\d+)", raw)
            number = int(m.group(1)) if m else 0
        elif raw.startswith("+"):
            yield path, number, raw[1:]
            number += 1
        elif not raw.startswith("-"):
            number += 1


def check_patch(deny, patch, label):
    problems = []
    for path, number, text in added_lines(patch):
        if path.startswith("docs/protocol/captures/"):
            continue
        for category in sorted(deny.scan(text)):
            problems.append(f"{label}{path}:{number}: a {category} value from the captures folder")
    return problems


def check_message(deny, text, label):
    problems = []
    for number, line in enumerate(text.split("\n"), 1):
        if line.startswith("#"):
            continue
        for category in sorted(deny.scan(line)):
            problems.append(f"{label}message line {number}: a {category} value from the captures folder")
    return problems


def check_commits(deny, revisions):
    problems = []
    for sha in revisions:
        short = sha[:9]
        problems += check_message(deny, git("log", "-1", "--format=%B", sha), f"{short} ")
        problems += check_patch(deny, git("show", "--format=", "-U0", "--no-color", "--no-ext-diff", sha),
                                f"{short} ")
    return problems


def outgoing(remote):
    revisions = []
    for line in sys.stdin.read().splitlines():
        parts = line.split()
        if len(parts) != 4 or parts[1] == ZERO:
            continue
        local, remote_sha = parts[1], parts[3]
        spec = [local, "--not", f"--remotes={remote}"] if remote_sha == ZERO else [f"{remote_sha}..{local}"]
        revisions += git("rev-list", *spec).split()
    return list(dict.fromkeys(revisions))


def warn_if_stale(captures):
    """A capture newer than the denylist may hold values it has never seen. A warning, not a refusal: the
    commit may have nothing to do with the new capture, and blocking on housekeeping teaches bypassing."""
    built = os.path.getmtime(DENYLIST)
    for top, dirs, files in os.walk(captures, followlinks=True):
        dirs[:] = [d for d in dirs if d not in (".venv", "venv", "site-packages", "node_modules", "__pycache__")]
        for name in files:
            try:
                if os.path.getmtime(os.path.join(top, name)) > built:
                    print("leak-guard: the captures folder has changed since the denylist was built; run "
                          "tools/leak-guard/build-denylist.py", file=sys.stderr)
                    return
            except OSError:
                continue


def main():
    root = git("rev-parse", "--show-toplevel").strip()
    os.chdir(root)
    captures = os.path.join("docs", "protocol", "captures")
    if not os.path.isdir(captures):
        return 0   # a clone without the captures folder: nothing to check against
    if not os.path.exists(DENYLIST):
        print("leak-guard: the captures folder has no denylist; run tools/leak-guard/build-denylist.py",
              file=sys.stderr)
        return 1
    deny = Denylist(DENYLIST)
    warn_if_stale(captures)

    mode = sys.argv[1] if len(sys.argv) > 1 else "--staged"
    if mode == "--staged":
        problems = check_patch(deny, git("diff", "--cached", "-U0", "--no-color", "--no-ext-diff"), "")
    elif mode == "--message":
        with open(sys.argv[2], encoding="utf-8", errors="replace") as f:
            problems = check_message(deny, f.read(), "")
    elif mode == "--push":
        problems = check_commits(deny, outgoing(sys.argv[2] if len(sys.argv) > 2 else "origin"))
    elif mode == "--history":
        problems = check_commits(deny, git("rev-list", "--branches", "--tags").split())
    else:
        print(__doc__, file=sys.stderr)
        return 2

    if problems:
        print("leak-guard: refused. These carry values recorded in the captures folder; replace each with a "
              "placeholder or a synthetic value (docs/README.md, \"Redaction placeholders\"):", file=sys.stderr)
        for p in problems[:50]:
            print("  " + p, file=sys.stderr)
        if len(problems) > 50:
            print(f"  ... and {len(problems) - 50} more", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
