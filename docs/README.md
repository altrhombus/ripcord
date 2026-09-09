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

The published documents are written to stand alone without it. Values tied to a particular console,
account or session are redacted to stable placeholders; public IP addresses are replaced with
[RFC 5737](https://www.rfc-editor.org/rfc/rfc5737) documentation addresses. If you find a value in the
published tree that identifies real hardware or a real account, that is a bug — please report it as one,
privately, per [`SECURITY.md`](../SECURITY.md).

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
