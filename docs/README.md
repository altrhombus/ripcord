# Ripcord documentation

Start here if you are not sure which file you want. Ripcord keeps several long-lived documents, and the
distinction between them is *the question each one answers* — not the topic. Several cover the same
subject from different angles, which is why picking by topic alone leads you to the wrong file.

## Which document answers which question

| Your question | Read |
|---|---|
| What is this, does it work, can I run it? | [`README.md`](../README.md) |
| How is the code arranged, and what rules keep it that way? | [`architecture.md`](architecture.md) |
| What does the app look like, and what decides that? | [`design.md`](design.md) |
| What is the macOS client, what is it built on, and in what order? | [`macos-plan.md`](macos-plan.md) |
| What about iPhone, iPad, Apple TV and Apple Watch? | [`ios-plan.md`](ios-plan.md) |
| What does the Mac app look like, and what decides that? | [`src/Ripcord.Mac/DESIGN.md`](../src/Ripcord.Mac/DESIGN.md) |
| How do I build, test and release the Mac app? | [`src/Ripcord.Mac/README.md`](../src/Ripcord.Mac/README.md) |
| Which protocol engine does each client run, and how does the project get to one? | [`engine-plan.md`](engine-plan.md) |
| How do I build and test the Rust engine, and where does it stand? | [`engine/README.md`](../engine/README.md) |
| **What is left to do?** | [`ROADMAP.md`](../ROADMAP.md) |
| **What happened, and when?** | [`journal.md`](journal.md) |
| **What outside material was consulted, and what did each item inform?** | [`protocol-research-log.md`](protocol-research-log.md) |
| How does the PS5 Remote Play protocol actually work? | [`protocol/`](protocol/) |
| In what order would I build a client from the spec? | [`protocol/IMPLEMENTATION.md`](protocol/IMPLEMENTATION.md) |
| What is the portable C core the console ports share? | [`libripcord/README.md`](../libripcord/README.md) |
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
- **`protocol-research-log.md` is the provenance record.** One row per external reference
  consulted, what it informed, and which spec section it fed. It exists to make the independence claim
  checkable by someone who does not trust it. It is evidence, not narrative.

### The console ports

`ports/` is documented inside its own tree rather than here. A port's setup is inseparable from its
toolchain, and a second copy of it under `docs/` would go stale the first time a devkit moved.
[`architecture.md`](architecture.md) has the layering and the rule about what may be shared with `src/`;
these answer "what is it, and how do I build it".

| Document | Answers |
|---|---|
| [`libripcord/README.md`](../libripcord/README.md) | What the portable C99 core holds, and what the platform seam does and does not ask of an OS |
| [`ports/ripcord-3ds/README.md`](../ports/ripcord-3ds/README.md) | The 3DS port: status, design, what runs on hardware |
| [`ports/ripcord-3ds/SETUP.md`](../ports/ripcord-3ds/SETUP.md) | Building it and getting it onto a console |
| [`ports/ripcord-3ds/HARDWARE-PROBES.md`](../ports/ripcord-3ds/HARDWARE-PROBES.md) | What each on-device probe measured |
| [`ports/ripcord-ps3/README.md`](../ports/ripcord-ps3/README.md) | The PS3 port: status, design, what runs on hardware |
| [`ports/ripcord-ps3/SETUP.md`](../ports/ripcord-ps3/SETUP.md) | Toolchain, packaging, and the fast iteration loop |
| [`ports/ripcord-ps3/DECODE.md`](../ports/ripcord-ps3/DECODE.md) | The decoder investigation — why the console's own decoder, what it accepts, and the values no SDK header states |
| [`ports/ripcord-ps3/SHELL-DESIGN.md`](../ports/ripcord-ps3/SHELL-DESIGN.md) | The shell's design plan — what the on-console interface should be, and what nine comments in `source/ui/` argue with |

## What is deliberately not here

`docs/protocol/captures/` — the "captures folder" — is gitignored and never published. It holds raw packet
captures, the working lab notebook with unredacted values, vendor↔ours name mappings, and
provenance-audit findings. Documents in the published tree cite it by filename so the trail is followable
by anyone who has their own captures, but its contents do not ship.

**Do not cite commit hashes in these documents.** The history was rewritten before publication, so any
pre-publication hash the docs might carry resolves to nothing. Such citations look like
verifiable evidence and were not, which is worse than no citation at all in a project whose central claim is
that its derivation is checkable. Cite a **date and a document section** instead: a squash cannot break
either. Where a specific change matters, name what it did and when, and let `git log` find it.

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
  protocol-research-log.md     provenance: what was consulted
  protocol/                    the wire-protocol specification
    README.md                  spec index and provenance
    IMPLEMENTATION.md          build order and definition of done
    ps5-*.md                   the specification proper
    dualsense-hid-report.md    controller HID report formats
    *.proto                    schemas, compiled into the build
    captures/                  CAPTURES FOLDER — gitignored, never published
  history/                     superseded plans, kept for the record
```

Port documentation is **not** under `docs/` — it lives beside each port in `ports/*/`, for the reason
given in [The console ports](#the-console-ports).
