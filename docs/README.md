# Ripcord documentation

Start here if you are not sure which file you want. Ripcord keeps several long-lived documents, and the
distinction between them is *the question each one answers* — not the topic. Several cover the same
subject from different angles, which is why picking by topic alone leads you to the wrong file.

## Which document answers which question

| Your question | Read |
|---|---|
| What is this, does it work, can I run it? | [`README.md`](../README.md) |
| How is the code arranged, and what rules keep it that way? | [`architecture.md`](architecture.md) |
| **What is left to do?** | [`ROADMAP.md`](../ROADMAP.md) |
| **What happened, and when?** | [`journal.md`](journal.md) |
| **What outside material was consulted, and what did each item inform?** | [`protocol-research-log.md`](protocol-research-log.md) |
| How does the PS5 Remote Play protocol actually work? | [`protocol/`](protocol/) |
| In what order would I build a client from the spec? | [`protocol/IMPLEMENTATION.md`](protocol/IMPLEMENTATION.md) |
| What is the portable C core the console ports share? | [`ports/common/README.md`](../ports/common/README.md) |
| How do I build or run a console port? | [The console ports](#the-console-ports), below |
| How do I contribute, and what must I attest to? | [`CONTRIBUTING.md`](../CONTRIBUTING.md) |
| How was it built, historically? | [`history/`](history/) |

### The three that are easily confused

`ROADMAP.md`, `journal.md` and `protocol-research-log.md` all look like running logs. They are not
interchangeable:

- **`ROADMAP.md` is the open backlog**, plus a status preamble and whatever context an open item needs to be
  understood — and no historical record beyond that. When an item is finished, its story moves to the journal
  and a pointer stays behind, so the roadmap stays short enough to be read in full.
- **`journal.md` is the dated engineering record.** What was tried, what broke, what the fix turned out to
  be. It is a *record*, not a reference: where it states a protocol fact, [`protocol/`](protocol/) is
  authoritative and the journal may be out of date.
- **`protocol-research-log.md` is the clean-room provenance record.** One row per external reference
  consulted, what it informed, and which spec section it fed. It exists to make the independence claim
  checkable by someone who does not trust it. It is evidence, not narrative.

### The console ports

`ports/` is documented inside its own tree rather than here. A port's setup is inseparable from its
toolchain, and a second copy of it under `docs/` would go stale the first time a devkit moved.
[`architecture.md`](architecture.md) has the layering and the rule about what may be shared with `src/`;
these answer "what is it, and how do I build it".

| Document | Answers |
|---|---|
| [`ports/common/README.md`](../ports/common/README.md) | What the portable C99 core holds, and what the platform seam does and does not ask of an OS |
| [`ports/ripcord-3ds/README.md`](../ports/ripcord-3ds/README.md) | The 3DS port: status, design, what runs on hardware |
| [`ports/ripcord-3ds/SETUP.md`](../ports/ripcord-3ds/SETUP.md) | Building it and getting it onto a console |
| [`ports/ripcord-3ds/HARDWARE-PROBES.md`](../ports/ripcord-3ds/HARDWARE-PROBES.md) | What each on-device probe measured |
| [`ports/ripcord-ps3/README.md`](../ports/ripcord-ps3/README.md) | The PS3 port: status, design, what runs on hardware |
| [`ports/ripcord-ps3/SETUP.md`](../ports/ripcord-ps3/SETUP.md) | Toolchain, packaging, and the fast iteration loop |
| [`ports/ripcord-ps3/DECODE.md`](../ports/ripcord-ps3/DECODE.md) | The decoder investigation — why the console's own decoder, what it accepts, and the values no SDK header states |
| [`ports/ripcord-ps3/SHELL-DESIGN.md`](../ports/ripcord-ps3/SHELL-DESIGN.md) | The shell's design plan — what the on-console interface should be, and what nine comments in `source/ui/` argue with |

## What is deliberately not here

`docs/protocol/captures/` — the "dirty room" — is gitignored and never published. It holds raw packet
captures, the working lab notebook with unredacted values, vendor↔ours name mappings, and
provenance-audit findings. Documents in the published tree cite it by filename so the trail is followable
by anyone who has their own captures, but its contents do not ship.

**Do not cite commit hashes in these documents.** The history was rewritten before publication, so any
pre-publication hash the docs might carry resolves to nothing. Such citations look like
verifiable evidence and were not, which is worse than no citation at all in a project whose central claim is
that its derivation is checkable. Cite a **date and a document section** instead: a squash cannot break
either. Where a specific change matters, name what it did and when, and let `git log` find it.

**What the git history discloses, and why it is not being rewritten.** The guards in this repository all
read file contents. A clone carries three things that are not files and that no check here can see: commit
messages, author identity, and commit timestamps. The first is now swept — the test suite runs every commit
message through the same detector, because a message caught before a push is one `git commit --amend` and a
message caught after is a history rewrite. The other two are inherent to git and are recorded here as known:
authorship is a pseudonym and a `@users.noreply.github.com` address, and **every commit carries a `-0500` UTC
offset**, which across July–September places the author in North American Central Time, with a visible
evening working pattern.

That is real information about the author and it is not removable without rewriting every commit. **The
project owner has looked at it and accepted it, twice** — the second time knowing that a rewrite was cheap,
because this repository has never been public and has no forks. The disclosure is a timezone band, and
rewriting every commit's dates is a categorically larger operation than the two targeted rewrites this
history has had: it would rewrite the one field those rewrites went out of their way to preserve, which is
what lets a reader check that the dated engineering record in `docs/journal.md` matches the commits behind
it. This is settled — a later review can note it, but it does not need re-deciding. Committing with
`TZ=UTC` avoids adding to it.

*(An earlier version of this paragraph argued the point from "putting a clean history back through the
rewrite machinery costs more than it buys". That reasoning was wrong and was retired when a targeted
message rewrite turned out to be free; the conclusion here does not rest on it. Counts of how many reviews
the history has passed are deliberately not stated, because that number keeps changing and this document
asks three paragraphs below that stated figures be re-derived when they do.)*

**`README.md`'s test figures are exact, so re-derive them when the count changes.** The total, the
clean-checkout figure and the sweep's own case count are all stated precisely, in a document whose value is
that a reader can check it — and they have gone stale twice, both times because a commit added tests without
touching prose. There is no automated check because a test asserting a number in a README is worse than the
staleness it prevents. The command is two lines, and the skip count is unaffected unless the new tests are
`Skippable`:

```
dotnet test tests/Ripcord.Protocol.Halyard.Tests/Ripcord.Protocol.Halyard.Tests.csproj
dotnet test tests/Ripcord.Presentation.Tests/Ripcord.Presentation.Tests.csproj
```

The published documents are written to stand alone without it. Values tied to a particular console,
account or session are redacted to stable placeholders; public IPv4 addresses are replaced with
[RFC 5737](https://www.rfc-editor.org/rfc/rfc5737) documentation addresses. Two conventions sit alongside
that and are easy to miss: **private RFC 1918 addresses are published as captured**, deliberately, because
they identify nothing outside the LAN they were on; and the one IPv6 endpoint value is replaced with a
synthetic ULA rather than an RFC 3849 documentation address, because what the socket hands you is a ULA and
the form is the interoperability fact. If you find a value in the published tree that identifies real
hardware or a real account, that is a bug — please report it as one,
privately, per [`SECURITY.md`](../SECURITY.md).

## Redaction placeholders

The canonical set. A value tied to one console, account, session or network is replaced with the narrowest
of these that fits, so a reader can tell what was removed without seeing it:

| Placeholder | Stands for |
|---|---|
| `<redacted>` | anything with no more specific form below |
| `<registkey-wire>` / `<registkey-hex>` / `<registkey-dec>` | the three encodings of one registration key |
| `<ps4-registkey>` / `<ps4-companion>` | the PS4 equivalents |
| `<handshake-key>` | a session handshake key |
| `<duid>` | a device unique id |
| `<console-ip>` / `<client-ip>` | an address that identifies real hardware |
| `<passcode>` | the console's 8-digit pairing passcode or 4-digit login PIN |
| `<hostname>` / `<fqdn>` | a name that identifies real hardware |
| `<base64>` | a base64 value whose content is per-account or per-session |

`PublishedTreeSweepTests` enforces the absence of the values; this table is what to replace them with.

## Layout

```
docs/
  README.md                    this file
  architecture.md              how the code is arranged
  journal.md                   the dated engineering record
  protocol-research-log.md     clean-room provenance: what was consulted
  protocol/                    the wire-protocol specification
    README.md                  spec index, provenance and legal position
    IMPLEMENTATION.md          build order and definition of done
    ps5-*.md                   the specification proper
    dualsense-hid-report.md    controller HID report formats
    *.proto                    schemas, compiled into the build
    captures/                  DIRTY ROOM — gitignored, never published
  history/                     superseded plans, kept for the record
```

Port documentation is **not** under `docs/` — it lives beside each port in `ports/*/`, for the reason
given in [The console ports](#the-console-ports).
