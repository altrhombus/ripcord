# Contributing to Ripcord

Thanks for wanting to help! Here's the quick version, then the details.

## Quick start

1. **Open an issue first** for anything big, so we don't end up building the same thing twice.
   [`ROADMAP.md`](ROADMAP.md) has what's planned, and [`docs/README.md`](docs/README.md) maps everything else.
2. **Build it.** The Windows app needs Windows, the .NET 10 SDK and a C++ toolchain; the Rust engine, the C
   core and both .NET test suites build anywhere, Linux and macOS included. [`README.md`](README.md) has the
   commands.
3. **Sign off every commit** with `git commit -s` (see [§1](#1-sign-off)).
4. **Touching protocol or crypto code?** Read [§2](#2-the-independence-rule) first. It's the one rule here that
   really matters.
5. **Run both test suites after you commit,** then push:

   ```
   dotnet test tests/Ripcord.Protocol.Halyard.Tests/Ripcord.Protocol.Halyard.Tests.csproj
   dotnet test tests/Ripcord.Presentation.Tests/Ripcord.Presentation.Tests.csproj
   ```

   After, not before: one test sweeps every commit message, so your message can't be checked until it
   exists. If it fails, `git commit --amend` fixes it, right up until you push.

Contributions are accepted under [Apache-2.0](LICENSE).

---

## 1. Sign-off

Every commit needs a `Signed-off-by` line matching its author, which `git commit -s` adds:

```
Signed-off-by: Your Name <your.email@example.com>
```

By signing off you certify the [Developer Certificate of Origin 1.1](https://developercertificate.org/): you
wrote the contribution, or otherwise have the right to submit it under this project's licence.

(A DCO records where a contribution came from; it doesn't assign copyright. So once outside contributions
are merged, the project can't be relicensed on its own say-so. That's a trade-off worth knowing up front.)

---

## 2. The independence rule

Ripcord's protocol was worked out **independently**, from our own captures, our own analysis of the vendor
client and public references. That's the reason the project can exist. One contribution that imports another
implementation's code or constants damages that for everyone, and it's hard to undo once merged.

So for any pull request touching `Ripcord.Protocol.Halyard*`, `Ripcord.Cloud.Halyard`, `docs/protocol/`,
`engine/`, `libripcord/`, `ports/` or anything cryptographic, the PR template asks you to confirm three things:

1. You didn't consult, copy, quote or translate another implementation of these protocols.
2. You didn't get around point 1 with an AI assistant: no asking a model to fetch, recall or reproduce
   another implementation's source, constants or byte-level detail. **What matters is what gets obtained,
   not whether a model was involved.**
3. Anything you couldn't derive yourself is marked unconfirmed (`[X]`), not filled in from somewhere else.

**AI assistance is fine, and it's how most of this project was built.** Using a model to write code, read this
repository, reason about your own captures or work from public references is all welcome. Point 2 rules out
exactly one thing: using a model as a back door into someone else's codebase.

### Where facts can come from

- **Yes:** your own captures of your own console on your own account; your own analysis of the vendor client,
  on hardware you own; the committed spec in [`docs/protocol/`](docs/protocol/); and public references (RFCs,
  NIST test vectors, platform documentation, published papers).
- **No:** another Remote Play client's source in any form, a paraphrase or a translation included; constants
  or tables lifted from one; and its invented names for protocol concepts, which are a dead giveaway.

A heads-up if you'd analyse the vendor client yourself: its licence may prohibit reverse engineering. Whether
to do that is your decision, and your risk.

### Comparing is allowed, adopting isn't

Reading another implementation *to compare it with this one*, a provenance or similarity audit, is a
different thing from deriving from it, and it's allowed. The output is **findings, never code or spec text**,
and it must never hand you a fact you didn't already have. If a comparison shows another implementation has an
answer we lack, that's an open question: mark it `[X]` and derive it. Say in the PR if that's what you were
doing.

### Why the AI point is subtle

A language model may have another implementation in its training data, and can reproduce its structure,
constants or vocabulary without being asked and without saying so. So you can end up with borrowed detail
while believing you derived it. Some habits that help:

- If a model hands you a magic constant, a byte offset or an odd name you can't trace to a capture, your own
  analysis or a public reference, **don't keep it**. Mark it unconfirmed, or derive it.
- Be suspicious of fluent, confident specificity about wire formats. That's what recall looks like.
- Get the value from your own evidence first, then check it against anything else. In that order the fact is
  yours. In the other order it isn't, even when the bytes match.
- You're responsible for everything you submit, whatever produced it.

**Not sure whether something's contaminated?** Say so in the PR. A flagged question is cheap to answer; an
unflagged one found later puts every line around it under suspicion.

---

## 3. Practical bits

- **Tests.** Keep both suites green and add coverage for what you change. Some tests read real captures held
  outside the repository and skip themselves when they're missing. That's expected.
- **Never commit** captures, key material or vendor binaries. `docs/protocol/captures/` is gitignored and stays
  that way. The test is **generic versus personal**: anything tied to one console, account or session stays
  out. Two generic sets of values are committed on purpose (the v1 interop constants and the application OAuth
  credential), and widening that needs [`NOTICE`](NOTICE) and [`CLAUDE.md`](CLAUDE.md) amended together, so
  raise it in an issue first.
- **Leak-shaped test values are synthetic.** When you widen a detector, illustrate it with a made-up value,
  never a real one. `PublishedTreeSweepTests.cs` keeps a table of fixtures for exactly this.
- **Strings go in the catalogue,** with three exceptions: exception messages for programmer error, the
  diagnostics report, and protocol or product identifiers (which stay verbatim). The same token can be both:
  `"Ps5"` on the wire must never share a resource entry with `"PS5"` on a card. `LocalizationTests` checks it.
- **Adding a language** needs no code, just two files: copy
  `src/Ripcord.Presentation/Resources/Strings.resx` to `Strings.<culture>.resx`, and
  `src/Ripcord.App/Strings/en-US/Resources.resw` to `Strings/<culture>/Resources.resw`, then translate the
  `<value>` elements. Ripcord ships English only for now, since an unreviewed machine translation is worse than
  honest English, so a real one from a speaker is very welcome!
- **Style.** Match the code around you. Comments explain *why*, and in protocol code cite the evidence for a
  value: a capture, a spec section, or an address in the vendor binary. A comment that calls something
  confirmed when it isn't counts as a bug.
- **When work lands,** take it off [`ROADMAP.md`](ROADMAP.md) and record it in
  [`docs/journal.md`](docs/journal.md).
