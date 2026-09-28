#!/usr/bin/env python3
"""The leak guard: refuse a commit or a push that carries a value from the captures folder.

Called by the hooks in .githooks (`git config core.hooksPath .githooks` turns them on):
  check.py --staged          pre-commit: the lines this commit adds
  check.py --message FILE    commit-msg: the commit message
  check.py --push REMOTE     pre-push: every outgoing commit's added lines and message (refs on stdin)
  check.py --history         every commit on every local branch, for an audit by hand
  check.py --allow CATEGORY VALUE REASON
                             record an exception in the captures folder's leak-allow.tsv

It reads docs/protocol/captures/leak-denylist.tsv, which build-denylist.py writes from the captures folder and which
is never committed. With no captures folder, as in any clone but the owner's, it does nothing. PublishedTreeSweepTests
is the check that needs no secrets and runs in CI; this is the one that knows the real values.

A match is reported by where it is and what kind of value it is, never by the value, so the report is safe to
paste anywhere. LEAK_GUARD_SHOW=1 adds the value, for your own terminal, when a line is long enough that
"a hex value" does not say which.

EXCEPTIONS. A value that is denied and should not be (a synthetic fixture that happens to match, a public
server address you mean to cite) goes in docs/protocol/captures/leak-allow.tsv, one per line:
category, value, and the reason, tab-separated. It lives in the captures folder beside the denylist, because the
value is one the captures hold. Both scripts read it: the builder leaves the value out, and this script drops
it on load, so an exception works at once. `--allow` writes the line for you. A committed value also leaves the
denylist on its next build, because the builder subtracts whatever the tree already carries; the allow file is
for the commit that puts it there. Addresses are recognised in every spelling the protocol uses (dotted, the Host header's padded
columns, byte arrays in decimal or hex) and hex values through any separators, so reformatting a value does
not hide it.
"""

import os
import re
import subprocess
import sys

DENYLIST = os.path.join("docs", "protocol", "captures", "leak-denylist.tsv")
ALLOWLIST = os.path.join("docs", "protocol", "captures", "leak-allow.tsv")
CATEGORIES = ("ipv4", "account-id", "number", "online-id", "name", "ssid", "mac", "email", "hex")
ZERO = "0" * 40

DOTTED = re.compile(r"(?<![\d.])(\d{1,3})\. {0,2}(\d{1,3})\. {0,2}(\d{1,3})\. {0,2}(\d{1,3})(?![\d.])")
# Exactly four elements inside brackets, braces or parentheses: [172, 31, 0, 1], { 0xac, ... },
# Ipv4Addr::new(10, 0, 0, 1). Four numbers inside a longer argument list are data, not an address.
BYTES = re.compile(r"[\[{(]\s*(0x[0-9a-fA-F]{1,2}|\d{1,3})\s*,\s*(0x[0-9a-fA-F]{1,2}|\d{1,3})\s*,\s*"
                   r"(0x[0-9a-fA-F]{1,2}|\d{1,3})\s*,\s*(0x[0-9a-fA-F]{1,2}|\d{1,3})\s*[\]})]")
HEX_RUN = re.compile(r"[0-9a-fA-F](?:[ :,._-]?[0-9a-fA-F])*")
DIGITS = re.compile(r"(?<!\d)\d{19}(?!\d)")


def read_allowed(path=ALLOWLIST):
    """(category, value) pairs excepted from the denylist, each recorded with its reason."""
    allowed = set()
    if os.path.exists(path):
        with open(path, encoding="utf-8") as f:
            for line in f:
                parts = line.rstrip("\n").split("\t")
                if len(parts) >= 2 and not line.startswith("#"):
                    allowed.add((parts[0], parts[1].lower()))
    return allowed


class Denylist:
    def __init__(self, path, allowed=frozenset()):
        self.ipv4, self.numbers, self.words, self.hex = set(), {}, {}, {}
        with open(path, encoding="utf-8") as f:
            for line in f:
                if line.startswith("#") or "\t" not in line:
                    continue
                category, value = line.rstrip("\n").split("\t", 1)
                if (category, value.lower()) in allowed:
                    continue
                if category == "ipv4":
                    self.ipv4.add(value)
                elif category in ("account-id", "number") and value.isdigit():
                    self.numbers[value] = category
                elif category in ("hex", "mac") or re.fullmatch(r"[0-9a-f]{16}", value):
                    self.hex.setdefault(len(value), {})[value] = category
                else:
                    self.words[value.lower()] = category
        self.lengths = sorted(self.hex)
        self.word_rx = (re.compile(r"(?<![\w.-])(" + "|".join(re.escape(w) for w in sorted(self.words, key=len,
                                                                                            reverse=True))
                                   + r")(?![\w-])", re.IGNORECASE) if self.words else None)

    def scan(self, line):
        """(category, value) for each denied value this line carries."""
        hits = set()
        for rx in (DOTTED, BYTES):
            for m in rx.finditer(line):
                try:
                    octets = [int(g, 16) if g.lower().startswith("0x") else int(g) for g in m.groups()]
                except ValueError:
                    continue
                address = ".".join(map(str, octets))
                if all(o <= 255 for o in octets) and address in self.ipv4:
                    hits.add(("ipv4", address))
        for m in DIGITS.finditer(line):
            if m.group(0) in self.numbers:
                hits.add((self.numbers[m.group(0)], m.group(0)))
        if self.word_rx:
            for m in self.word_rx.finditer(line):
                hits.add((self.words[m.group(1).lower()], m.group(1)))
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
                            hits.add((category, flat[i:i + n]))
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


def shown(value):
    return f" ({value})" if os.environ.get("LEAK_GUARD_SHOW") == "1" else ""


def check_patch(deny, patch, label):
    problems = []
    for path, number, text in added_lines(patch):
        if path.startswith("docs/protocol/captures/"):
            continue
        for category, value in sorted(deny.scan(text)):
            problems.append(f"{label}{path}:{number}: a {category} value from the captures folder{shown(value)}")
    return problems


def check_message(deny, text, label):
    problems = []
    for number, line in enumerate(text.split("\n"), 1):
        if line.startswith("#"):
            continue
        for category, value in sorted(deny.scan(line)):
            problems.append(f"{label}message line {number}: a {category} value from the captures folder{shown(value)}")
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
            if name in ("leak-denylist.tsv", "leak-allow.tsv"):
                continue
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
    mode = sys.argv[1] if len(sys.argv) > 1 else "--staged"
    if mode == "--allow":
        if len(sys.argv) != 5 or sys.argv[2] not in CATEGORIES or not sys.argv[4].strip():
            print("usage: check.py --allow CATEGORY VALUE REASON   (category: " + ", ".join(CATEGORIES) + ")",
                  file=sys.stderr)
            return 2
        new = not os.path.exists(ALLOWLIST)
        with open(ALLOWLIST, "a", encoding="utf-8") as f:
            if new:
                f.write("# Exceptions to the leak guard's denylist: category, value, reason. Never commit this.\n")
            f.write(f"{sys.argv[2]}\t{sys.argv[3]}\t{sys.argv[4].strip()}\n")
        os.chmod(ALLOWLIST, 0o600)
        print(f"leak-guard: {sys.argv[2]} excepted; recorded in {ALLOWLIST}")
        return 0

    deny = Denylist(DENYLIST, read_allowed())
    warn_if_stale(captures)

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
