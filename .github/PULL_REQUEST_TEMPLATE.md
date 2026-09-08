## What this changes

<!-- What it does and why. If it fixes an issue, "Fixes #123". -->

## How it was verified

<!-- Delete what does not apply. "Builds and passes tests" is not verification for anything
     touching hardware, the UI, or the wire — say what you actually observed. -->

- [ ] `dotnet test` — both suites green
- [ ] Driven against a real console (say which: PS5 / PS4, LAN / internet)
- [ ] Driven through the app's UI, not only tests or the harness
- [ ] Checked on ARM64 as well as x64
- [ ] Not verified on hardware — and the PR says so where it matters

## Sign-off

- [ ] Every commit carries `Signed-off-by:` matching the author (`git commit -s`) — see
      [CONTRIBUTING §1](../CONTRIBUTING.md#1-developer-certificate-of-origin)

## Clean-room attestation

**Required for anything touching `Ripcord.Protocol.Halyard*`, `Ripcord.Cloud.Halyard`, `docs/protocol/`,
or cryptography. Delete this whole section if your PR touches none of them.**

Read [CONTRIBUTING §2](../CONTRIBUTING.md#2-clean-room-attestation--read-this-properly) before ticking
these. AI assistance is allowed and is how most of this project was built — point 2 restricts exactly one
thing, and the boundary is *what gets obtained*, not whether a model was involved.

- [ ] I did **not** consult, copy from, quote, or translate the source of any other implementation of
      these protocols while writing this.
- [ ] I did **not** route around that using an AI assistant — I did not ask a model to fetch, recall,
      quote, or reproduce another implementation's source, constants, or byte-level detail.
- [ ] Anything I could not derive myself is **marked unconfirmed** (`[X]`) in the code or spec rather
      than filled in from an outside source.

If any value in this PR cannot be traced to a capture, this project's own binary analysis, or a public
reference (RFC, NIST vector, vendor API docs), say so here rather than leaving it to review:

<!-- ... -->
