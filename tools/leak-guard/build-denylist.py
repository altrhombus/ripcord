#!/usr/bin/env python3
"""Build the leak guard's denylist from the dirty room.

The published-tree sweep (PublishedTreeSweepTests) catches values by their shape. It cannot know which values
are real, because the list of real ones is itself personal and cannot be committed. This script builds that
list where it belongs, beside the captures, and tools/leak-guard/check.py reads it from the git hooks. So a
commit is checked against the owner's actual addresses, names and ids before it leaves the machine.

WHAT IT READS. The dirty room's own text: notes, logs, scripts, flows and JSON under docs/protocol/captures,
following the symlink. Vendored environments (.venv, site-packages, node_modules) are skipped: they are other
people's code, and their example addresses would only produce noise.

WHAT IT COLLECTS, by category:
  ipv4        every address, less loopback, multicast, the documentation ranges and the synthetic LANs
  account-id  19-digit numbers (PSN account ids)
  online-id   "onlineId" values
  name        console names: PS5-/PS4-Nickname, host-name, "name":"PS5-...", and default-style PS5-123
  ssid        AP-Ssid / AP-Name / "ssid" values
  mac         host ids and MACs, as 12 bare lowercase hex digits
  email       email addresses
  hex         runs of 32 or more hex digits: registration keys, device ids, session keys, nonces

WHAT IT LEAVES OUT. Any value already present in the committed tree or in a local branch's commit messages.
Those are the bundled interop constants, Client-Type, published test vectors and synthetic fixtures, all of
which the dirty room's scripts carry too. This assumes the tree is clean when the list is built, so the values
subtracted from the low-entropy categories are printed for a person to look at. Everything denied is never
printed.

Run it again whenever the dirty room gains a capture; check.py warns when the list is older than the newest
capture file.
"""

import ipaddress
import os
import re
import subprocess
import sys

TEXT_SUFFIXES = {".md", ".txt", ".py", ".log", ".json", ".js", ".tsv", ".csv", ".ps1", ".flows", ".har",
                 ".html", ".xml", ".yaml", ".yml", ".ini", ".cfg", ".conf", ""}
SKIP_DIRS = {".venv", "venv", "site-packages", "node_modules", "__pycache__", ".git"}
MAX_BYTES = 64 * 1024 * 1024
OUTPUT_NAME = "leak-denylist.tsv"

SYNTHETIC_LANS = [ipaddress.ip_network(n) for n in ("10.0.0.0/24", "172.31.0.0/24", "192.168.1.0/24")]
DOCUMENTATION = [ipaddress.ip_network(n) for n in ("192.0.2.0/24", "198.51.100.0/24", "203.0.113.0/24")]

IPV4 = re.compile(r"(?<![\d.])(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})(?![\d.])")
ACCOUNT_ID = re.compile(r"(?<!\d)\d{19}(?!\d)")
ONLINE_ID = re.compile(r'"onlineId"\s*:\s*"([^"]{3,40})"')
NAME_FIELDS = re.compile(
    r'(?:PS[45]-Nickname|host-name)\s*[:=]\s*"?([^"\r\n,]{2,40})'
    r'|"(?:name|deviceName|nickname)"\s*:\s*"(PS[45][^"]{1,40})"'
    r"|\b(PS[45]-\d{3})\b", re.IGNORECASE)
SSID_FIELDS = re.compile(r'(?:AP-Ssid|AP-Name)\s*[:=]\s*"?([^"\r\n,]{3,40})|"ssid"\s*:\s*"([^"]{3,40})"',
                         re.IGNORECASE)
HOST_ID = re.compile(r"(?:host-id|PS[45]-Mac|AP-Bssid|macAddr\w*)\"?\s*[:=]\s*\"?([0-9a-fA-F:.-]{12,17})",
                     re.IGNORECASE)
MAC = re.compile(r"(?<![0-9a-fA-F:-])([0-9a-fA-F]{2}(?:[:-][0-9a-fA-F]{2}){5})(?![0-9a-fA-F:-])")
EMAIL = re.compile(r"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")
LONG_HEX = re.compile(r"(?<![0-9a-fA-F])([0-9a-fA-F]{32,})(?![0-9a-fA-F])")

LOW_ENTROPY = ("ipv4", "account-id", "online-id", "name", "ssid", "mac", "email")
PLACEHOLDER = re.compile(r"^<.*>$|redacted|example|placeholder|^x+$", re.IGNORECASE)


def repo_root():
    return subprocess.run(["git", "rev-parse", "--show-toplevel"], check=True, capture_output=True,
                          text=True).stdout.strip()


def dirty_room(root):
    return os.path.join(root, "docs", "protocol", "captures")


def own_text_files(captures):
    for top, dirs, files in os.walk(captures, followlinks=True):
        dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
        for name in files:
            if name == OUTPUT_NAME:
                continue
            path = os.path.join(top, name)
            if os.path.splitext(name)[1].lower() not in TEXT_SUFFIXES:
                continue
            try:
                if os.path.getsize(path) <= MAX_BYTES:
                    yield path
            except OSError:
                continue


def keep_ipv4(text):
    try:
        ip = ipaddress.ip_address(text)
    except ValueError:
        return False
    if ip.is_loopback or ip.is_multicast or ip.is_unspecified or ip.is_link_local or ip.is_reserved:
        return False
    if str(ip) == "255.255.255.255" or text.endswith(".0") or text.endswith(".255") or text.startswith("0."):
        return False
    # Four small numbers outside the private ranges are version and clause numbers far more often than
    # addresses, and denying them only teaches people to ignore the guard.
    if not ip.is_private and all(int(o) < 32 for o in text.split(".")):
        return False
    return not any(ip in n for n in SYNTHETIC_LANS + DOCUMENTATION)


def flat_mac(value):
    flat = re.sub(r"[^0-9a-fA-F]", "", value).lower()
    return flat if len(flat) == 12 and len(set(flat)) > 2 and flat != "001122334455" else None


def collect(text, out):
    for m in IPV4.finditer(text):
        if all(int(g) <= 255 for g in m.groups()) and keep_ipv4(m.group(0)):
            out.add(("ipv4", m.group(0)))
    for m in ACCOUNT_ID.finditer(text):
        if len(set(m.group(0))) > 3:
            out.add(("account-id", m.group(0)))
    for m in ONLINE_ID.finditer(text):
        out.add(("online-id", m.group(1).strip().lower()))
    for m in NAME_FIELDS.finditer(text):
        value = next(g for g in m.groups() if g).strip()
        if not PLACEHOLDER.search(value):
            out.add(("name", value.lower()))
    for m in SSID_FIELDS.finditer(text):
        value = next(g for g in m.groups() if g).strip()
        if not PLACEHOLDER.search(value) and value.upper() not in ("PS5", "PS4"):
            out.add(("ssid", value.lower()))
    for rx in (HOST_ID, MAC):
        for m in rx.finditer(text):
            flat = flat_mac(m.group(1))
            if flat:
                out.add(("mac", flat))
    for m in EMAIL.finditer(text):
        if not PLACEHOLDER.search(m.group(0)):
            out.add(("email", m.group(0).lower()))
    for m in LONG_HEX.finditer(text):
        value = m.group(1).lower()
        if len(set(value)) > 4:
            out.add(("hex", value))


def published_corpus(root):
    """Everything already public by construction: the tracked tree, and every local branch's messages."""
    files = subprocess.run(["git", "ls-files", "-z"], cwd=root, check=True, capture_output=True).stdout
    parts = []
    for rel in files.decode("utf-8", "replace").split("\0"):
        if not rel or rel.startswith("docs/protocol/captures"):
            continue
        try:
            with open(os.path.join(root, rel), "rb") as f:
                parts.append(f.read().decode("utf-8", "replace"))
        except OSError:
            continue
    messages = subprocess.run(["git", "log", "--branches", "--format=%B"], cwd=root, check=True,
                              capture_output=True).stdout.decode("utf-8", "replace")
    parts.append(messages)
    return "\n".join(parts).lower()


def already_published(corpus, found):
    """Which found values the corpus already carries. Addresses and account ids by exact token, hex by
    substring through an index of 32-character windows, and the few low-entropy strings by substring."""
    ips = {m.group(0) for m in IPV4.finditer(corpus)}
    numbers = set(ACCOUNT_ID.findall(corpus))
    hex_blob = "\n".join(re.sub(r"[^0-9a-f]", "", r) for r in re.findall(r"[0-9a-f](?:[ :,_-]?[0-9a-f]){11,}", corpus))
    windows = {hex_blob[i:i + 32] for i in range(len(hex_blob) - 31)}
    present = set()
    for category, value in found:
        if category == "ipv4":
            hit = value in ips
        elif category == "account-id":
            hit = value in numbers
        elif category == "hex":
            hit = value[:32] in windows and value in hex_blob
        elif category == "mac":
            hit = value in hex_blob
        else:
            hit = value in corpus
        if hit:
            present.add((category, value))
    return present


def main():
    root = repo_root()
    captures = dirty_room(root)
    if not os.path.isdir(captures):
        sys.exit(f"no dirty room at {captures}")

    found = set()
    count = 0
    for path in own_text_files(captures):
        with open(path, "rb") as f:
            collect(f.read().decode("utf-8", "replace"), found)
        count += 1

    present = already_published(published_corpus(root), found)
    denied, subtracted = [], []
    for category, value in sorted(found):
        (subtracted if (category, value) in present else denied).append((category, value))

    output = os.path.join(captures, OUTPUT_NAME)
    with open(output, "w", encoding="utf-8") as f:
        f.write("# The leak guard's denylist (tools/leak-guard). Built from this dirty room; never commit it.\n")
        for category, value in denied:
            f.write(f"{category}\t{value}\n")
    os.chmod(output, 0o600)

    by_category = {}
    for category, _ in denied:
        by_category[category] = by_category.get(category, 0) + 1
    print(f"read {count} files; denied {len(denied)} values: "
          + ", ".join(f"{k} {v}" for k, v in sorted(by_category.items())))
    low = [(c, v) for c, v in subtracted if c in LOW_ENTROPY]
    if low:
        print("left out because the committed tree already has them (check each is synthetic):")
        for category, value in low:
            print(f"  {category:10} {value}")
    print(f"wrote {output}")


if __name__ == "__main__":
    main()
