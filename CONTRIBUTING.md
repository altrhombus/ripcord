# Contributing to Ripcord

Contributions are welcome. Two requirements are non-negotiable and both are explained below: a **DCO
sign-off** on every commit, and a **clean-room attestation** on any pull request touching protocol or crypto
code. The second one is unusual, and it is the more important of the two for this project.

Please open an issue before starting anything large, so effort is not wasted on something that conflicts with
work already in flight. [`ROADMAP.md`](ROADMAP.md) is the source of truth for the open backlog; [`docs/README.md`](docs/README.md)
indexes everything else.

---

## 1. Developer Certificate of Origin

Every commit must carry a `Signed-off-by` line matching the author:

```
Signed-off-by: Your Name <your.email@example.com>
```

`git commit -s` adds it. By signing off you certify the
[Developer Certificate of Origin 1.1](https://developercertificate.org/) — in short, that you wrote the
contribution or otherwise have the right to submit it under this project's licence.

Contributions are accepted under [Apache-2.0](LICENSE), the project's licence.

> **Note on relicensing.** A DCO establishes the provenance of a contribution but does not assign copyright.
> Once outside contributions are merged, the project cannot be unilaterally relicensed. If that ever becomes
> necessary, a CLA would have to be introduced first, and existing contributors asked to agree. This is
> recorded here so the tradeoff is visible rather than discovered later.

---

## 2. Clean-room attestation — read this properly

Ripcord's legal position rests on its protocol specification having been derived **independently**. That is
not a slogan; it is the reason the project can exist. A single contribution that imports another
implementation's code or constants damages it for everyone, and the damage is not easily undone once merged.

So, for any PR touching anything cryptographic, state in the PR description that:

1. You did **not** consult, copy from, quote, or translate the source of any other implementation of these
   protocols while writing it, beyond the narrow design-level cross-check described below.
2. You did not route around point 1 using an AI assistant — that is, you did not ask a model to fetch,
   recall, quote, or reproduce another implementation's source, constants, or byte-level detail. **The
   boundary is what gets obtained, not whether a model was involved**: it is identical whether a human or a
   model crosses it.
3. Anything you could not derive yourself is **marked as unconfirmed** in the code or spec rather than filled
   in from an outside source.

**To be unambiguous: AI assistance is allowed, and is how most of this project was built.** Point 2 does not
restrict using a model to write code, read *this* repository, reason about *your own* captures, or work from
public references. It restricts exactly one thing — using a model as an indirect route to another codebase's
internals. See §3.

### Where facts may come from

**Acceptable:** your own packet captures, of your own console, on your own account · your own static analysis
of the vendor client, on hardware you own · this repository's committed spec under
[`docs/protocol/`](docs/protocol/) · public references — RFCs, NIST test vectors, platform documentation,
published papers.

**Not acceptable:** another Remote Play client's source, in any form, including a paraphrase or a translation
into another language · constants, tables, or magic values lifted from one · its invented names for protocol
concepts, which are a reliable fingerprint of exactly this kind of copying.

A third-party implementation may be used **only** to sanity-check a finding you already derived
independently, and **only** at the design-specification level — "which algorithm family is this," never a
byte-level construction, an exact constant, a code structure, or a name. This carve-out applies the same way
whichever route you take, human or model. Tag any such cross-check `[X]` in the spec and say where it came
from. **An `[X]` value is provisional, not settled**, and must not be described in code comments as
confirmed. A comment that overstates confidence is worse than no comment: it stops the next person checking.

### Auditing is not contamination

Reading another implementation *in order to compare it against this one* — a provenance or similarity audit —
is a different activity from deriving implementation detail, and it is permitted. It is also occasionally
necessary — periodically confirming that this project's independence claim still holds is worth doing, and
cannot be done without looking.

Two conditions make it an audit rather than a derivation:

- **The output is findings, never artefacts.** A report saying "these two implementations agree on X, and here
  is whether we can independently justify X" is fine. Copying X into code or spec text because the comparison
  revealed it is not.
- **It must not become a back door for an underived fact.** If an audit shows the other implementation has an
  answer you lack, you have found an open question — mark it `[X]`/unconfirmed and derive it. You have not
  found a value to adopt.

Say explicitly in the PR if this is what you were doing.

### If you are unsure whether something is contaminated

Say so in the PR. A flagged uncertainty is cheap to resolve. An unflagged one that surfaces later is
expensive, because it puts every neighbouring line under suspicion too.

---

## 3. AI assistance

**Use AI to do independent research and then confirm it.** That pattern — derive from your own captures
or your own analysis of the vendor binary, then verify against a second independent source — is the whole
method here, and a model is a perfectly good tool for it. Nothing in §2 narrows that.

**What §2 point 2 actually guards against is subtler than a rule about tools.** A language model may have
another Remote Play implementation in its training data, and can reproduce that implementation's structure,
constants, or invented vocabulary *without being asked to and without saying so*. You can therefore end up
with contaminated detail while believing you derived it. That is the risk — not the model's involvement, but
its capacity to launder provenance invisibly.

Practical guidance:

- Don't ask a model to fetch or recall another implementation's source or constants. (Auditing is the
  exception — see §2.)
- If a model produces a magic constant, a byte offset, or an unusual name you cannot trace to a capture, your
  own analysis, or a public reference — **do not keep it**. Mark it unconfirmed, or derive it.
- Be suspicious of fluent, confident specificity about wire formats. That is what recall looks like, and it is
  hard to tell from competence.
- Prefer the shape this project uses: get the value from your own evidence *first*, and only then check it
  against anything else. A fact confirmed in that order is yours; the same fact acquired in the other order
  is not, even when the bytes match.
- You remain responsible for everything you submit, whatever produced it.

---

## 4. Practical matters

**Tests.** Both suites are pure managed and run anywhere, with no console and no Windows-only hardware. CI
runs both, so run both:

```
dotnet test tests/Ripcord.Protocol.Halyard.Tests/Ripcord.Protocol.Halyard.Tests.csproj
dotnet test tests/Ripcord.Presentation.Tests/Ripcord.Presentation.Tests.csproj
```

Keep it green, and add coverage for behaviour you change. Some tests validate against real captured ground
truth held outside the repository and self-skip when absent — that is expected, not a failure.

**Run them after you commit, not before.** One of these tests sweeps every commit message, so your message
is part of what it checks and cannot be checked until it exists. Running the suite, committing, and pushing
in that order will pass locally and go red on the next clone — which is exactly how a purged value got back
into a published message once already. `commit → test → push` is the order; if it fails, `git commit
--amend` is the whole fix, and it stops being the whole fix the moment you push.

**And when you widen a detector, illustrate the new form with a synthetic value, never the real one.**
`PublishedTreeSweepTests.cs` keeps a table of deliberately leak-shaped fixtures for exactly this, and is
excluded from its own sweep so that examples can be written without writing secrets. The commit that added
underscore-separated matching demonstrated it with the real value and thereby became the first thing the
new rule caught.

**User-facing strings go in the catalogue; three kinds do not.** `Ripcord.Presentation` owns its text in
`Resources/Strings.resx`, reached through the generated `Strings` accessor, and `LocalizationTests` fails
the build if a key is unused, missing, or has no translator comment. Three categories stay hard-coded on
purpose: exception messages for programmer error, because they are read in bug reports and a translated
stack trace is less useful; the diagnostics report, because its audience is whoever is helping you; and
protocol or product identifiers, because CLAUDE.md already requires those verbatim and a translated wire
tag is a bug. The trap worth naming is that the *same token* can be both — `"Ps5"` as an on-wire
`host-type` must never share a resource entry with `"PS5"` as a caption on a card.

**Adding a language** needs no code, and there are two files because there are two layers. For the
portable one, copy `src/Ripcord.Presentation/Resources/Strings.resx` to `Strings.<culture>.resx`; for the
Windows UI, copy `src/Ripcord.App/Strings/en-US/Resources.resw` to `Strings/<culture>/Resources.resw`. In
both, translate the `<value>` elements and leave every `<data name>` alone. .NET resolves the satellite
assembly and MRT resolves the `.resw` from the user's `CurrentUICulture`. The split is not an accident:
MRT is Windows-only, and the portable layer has to stay linkable by a macOS or Linux front end, so its
strings are translated once and every front end inherits them. Ripcord ships English only, deliberately — an unreviewed machine translation is
worse than honest English, and the catalogue exists so a speaker can contribute a real one.

**Building** needs Windows plus a C++ toolchain; see [`README.md`](README.md). A contribution that only
touches managed protocol or core code can be developed and tested without the native half.

**Never commit** captures, key material, or vendor binaries. `docs/protocol/captures/` is gitignored and must
stay that way — it is the first rule in `.gitignore` for a reason. The test is **generic versus personal**,
not extracted versus derived: anything tied to a specific console, account or session stays out, whatever its
provenance. Two sets of *generic* extracted values are committed deliberately and under a stated argument —
the v1 interoperability constants and the application OAuth credential — and widening that is not a
contributor decision: it needs [`NOTICE`](NOTICE) and [`CLAUDE.md`](CLAUDE.md) amended together, so raise it
in an issue first. Check `git status`
before committing; if you have added a new tooling directory that might collect artifacts, add it to
`.gitignore` in the same commit.

**Style.** Match the surrounding code. Comments should explain *why*, and in protocol code should cite the
evidence for a value — a capture name, a spec section, or an address in the vendor binary. Comments that
assert a fact is confirmed when it is not are treated as bugs.

**Keep `ROADMAP.md` current** when work lands: move the finished item into [`docs/journal.md`](docs/journal.md)
rather than deleting it, so the dated record stays intact. The roadmap is forward-looking only, and is the
project's source of truth for what is
planned, and it is only useful if it is accurate.
