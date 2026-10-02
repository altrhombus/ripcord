# Ripcord for Mac — the plan

**The question this answers: what is the macOS client, what is it built on, and in what order?**

Settled 2026-09-24. This is a plan, and it is forward-looking. Work that lands moves to
[`journal.md`](journal.md) and the open items stay in [`../ROADMAP.md`](../ROADMAP.md), the same way as
everywhere else in the repository. Where this file and the roadmap disagree about what is still open, the
roadmap wins.

## The position

**A native Mac app, not a port of the Windows one.** It shares the protocol and the brand with the Windows
client. It shares no layout, component or flow. The test is that someone who uses a Mac every day already
knows how it works: double-click to connect, ⌘W to leave, ⌘, for Settings, the green button for full
screen, and secrets in the Keychain. Nothing in the app should need explaining.

What carries over from Windows is **what is true**, not what it looks like. `Ripcord.Presentation` encodes
lessons learned on hardware: "asked remotely" is not "woken", a failure names its cause in plain words,
and no user-visible control is inert. The Mac app re-derives those states from the protocol and uses the
Windows layer as a reference for them, not as code.

## Decisions (settled 2026-09-24)

| Question | Decision |
|---|---|
| Minimum OS | **macOS 26.** Liquid Glass is native there, so no second, glass-less version of each surface has to be designed |
| Architecture | **Apple Silicon only.** Same reasoning as the Windows client shipping x64 only: it is the architecture that can be tested |
| Distribution | **Developer ID, notarized, direct download** is the primary release. See "The App Store" below |
| First-release scope | **At least feature parity with the Windows client as of 2026-09-24**, including internet play and account sign-in |
| Protocol core | **The Rust engine** in [`engine-plan.md`](engine-plan.md), since Phase 3's relink on 2026-09-26. It was first `libripcord`, the portable C core, brought up to parity and proved on hardware; see "Why the C core" below for why a C-ABI core was chosen at all |
| Location | **`src/Ripcord.Mac/`**, beside `Ripcord.App` |
| Build | **An Xcode project, not a Swift package.** First because a package could not compile the C core from where it lived; the app and its extensions need a project anyway. See [`../src/Ripcord.Mac/README.md`](../src/Ripcord.Mac/README.md) |
| Key agreement | **CryptoKit**, behind the engine's backend table, checked against the .NET vectors through the engine (`EngineKeyAgreementTests`) |

## Why the C core

> **Superseded in part, 2026-09-25.** [`engine-plan.md`](engine-plan.md) keeps this section's conclusion
> that the Mac runs a native core behind a C ABI, with no second runtime and threads owned by the app, and
> changes which core: a Rust engine that presents the same contract as `halyard_client.h`. The Mac relinked
> onto it on 2026-09-26. CryptoKit stays as the Mac's key agreement. What follows is kept as the record of why a C-ABI core was chosen over .NET.

This was decided twice, and the first answer was wrong for a reason worth keeping. While the C core
lacked internet play and the account tier, the .NET stack looked like the better engine. It could be
compiled ahead-of-time into a native library and gave parity on day one. But that was an argument from
**features**, and features can be ported. So the question was put again: **if both cores were at parity,
which would this app choose?**

On its merits, the C core wins for a Mac client:

- **One toolchain, one runtime, one debugger.** Swift imports C headers directly, so the compiler checks
  the boundary. A .NET engine means a hand-maintained C API on both sides, a second runtime in the process,
  and C# that cannot be stepped through in-process.
- **It fits wherever Apple lets code run.** Widgets, Control Center controls and App Intents run in
  separate processes with tight memory limits, and a "wake" control needs the wake code in that process.
  C is a small link there. The same holds if an iPad, Apple TV or Vision Pro client ever follows.
- **The threads and the memory belong to the app.** There is no garbage collector, and no foreign thread
  pool calling into Swift 6's strict concurrency. The hot path already allocates nothing (see
  `libripcord/stream/stream_demux.h`).
- **Taking the core to parity pays off beyond this app.** The ports exist partly to test that the spec is
  complete. Taking the core to internet play and account pairing extends that test to parts no port has
  read yet, and puts internet play within reach of the console ports.

**What .NET would still have won on is memory safety**, because the C core parses network bytes, some of
them before anything is authenticated. The answer is to make that risk visible and guarded rather than
assumed away: the core now has fuzz harnesses for every such parser and a sanitizer run in CI (see
[`../libripcord/README.md`](../libripcord/README.md), "Fuzzing").

**A pure Swift core was considered and rejected.** It fits best and is memory-safe, but it would be a third
full implementation to keep in step forever. Its only real advantage over "C core plus a Swift layer" is
memory safety, and fuzzing covers most of that.

**The .NET stack stays the reference implementation.** `ProtocolLab vectors` keeps generating the
known-answer vectors the C core is tested against, as it does for the ports.

## The split

Divide by the kind of work, not by the language each piece happens to be in today:

| Layer | Lives in | Why |
|---|---|---|
| Wire protocol: transport, crypto, stream, FEC, pairing, wake, discovery, STUN, the internet-play connect sequence, account pairing | **The Rust engine** (first `libripcord`) | Bytes on UDP, the same for every client, and embeddable everywhere |
| Cloud tier: OAuth, the console list, cloud wake, signaling, the push WebSocket (`Ripcord.Cloud.Halyard`, about 2,700 lines) | **Swift** | JSON from the internet is where C parsing is least wanted. `URLSession`, `URLSessionWebSocketTask`, `Codable` and `ASWebAuthenticationSession` are the native tools, and sign-in is a system sheet on the Mac anyway |
| Session lifecycle: reconnect, watchdog, stats (`SessionController`, about 890 lines) | **Swift**, as the session actor | This is what Swift concurrency is for |
| Video, audio, input | **Swift** over Apple frameworks | VideoToolbox into `AVSampleBufferDisplayLayer`, Opus through Core Audio, the GameController framework |
| Credentials | **Keychain** | The Mac's DPAPI, and where a Mac user expects secrets to live |

## Parity: the Windows client as of 2026-09-24

The first release is not done until each of these works on the Mac:

- PIN pairing and account (no-PIN) pairing, for PS5 and PS4.
- LAN discovery (SRCH broadcast and mDNS), with the Local Network permission prompt designed into first
  run rather than met by surprise.
- LAN wake, cloud wake as the fallback, and the passcode prompt for a locked console.
- PSN sign-in and the account's console list.
- Internet play through STUN, peer to peer.
- H.264, HEVC and 10-bit decode.
- Audio.
- DualSense, Xbox pads and the keyboard, with configurable bindings and more than one device merged.
- The stream statistics the Windows HUD shows, in the Mac form (an inspector).

## What makes it a Mac app

These are the design direction's working hypotheses. A `DESIGN.md` beside the app will settle them before
the surfaces are built, the way [`design.md`](design.md) settled the Windows ones.

- **Library.** One window, consoles as tiles, in the shape of Screen Sharing's window. Return or double-click
  connects, ⌘N pairs, and the whole window can be driven from a controller.
- **The launch.** The chosen tile grows into the stream window, and the mark's three dashes fill in as three
  real connection stages complete. The mark becomes a custom SF Symbol, so the same progress appears in the
  menu bar and in widgets.
- **Stream window.** It works like QuickTime Player. The controls are a glass capsule that leaves on its
  own, the statistics live in an inspector (⌘I), and the green button gives native full screen.
- **Picture in Picture and recording.** PiP comes with `AVSampleBufferDisplayLayer`. Recording writes the
  console's own bitstream into a movie file with no re-encode.
- **Present across the Mac.** A menu bar extra, a desktop widget, a Control Center control, and App Intents
  for Spotlight and Shortcuts.
- **Stretch.** Scanning the pairing code off the TV through Continuity Camera (written 2026-09-26, not yet
  pointed at a console), pressing the PS button to
  start playing, HDR on XDR displays, and DualSense haptics and triggers.

## Open questions, to settle by measurement

Each of these is `[X]` in the sense the protocol docs use: assumed, never confirmed. None may be described
as working until it has been measured.

- **Game Mode.** Declaring the app as a game should give it Game Mode in full screen, and Game Mode is
  understood to raise Bluetooth controller polling. Measure both claims before the design leans on either.
- **The PS button.** Whether macOS 26 claims a controller's Home button for its own game overlay, and what
  background controller monitoring allows. "Press PS to play" depends on both.
- **Crypto throughput.** The C core's software AES-GCM has not been measured at 1080p60 on Apple Silicon.
  If it falls short, the fix is ARMv8 AES and polynomial-multiply intrinsics in the core, which the ports
  on ARM would also get.
- **DualSense output on the wire.** macOS has native APIs for haptics, adaptive triggers and the light bar.
  What the console *sends* for them has not been derived. That work is independent protocol work under the
  usual rules, not an Apple API question.
- **Threading.** How the C core's receive and pump loops map onto Swift concurrency: which threads the
  Swift side owns, and where the hand-offs sit.

## The App Store

**Not the primary release, and not ruled out.** Three guidelines bear on it. They were read in their
current text on 2026-09-24, not recalled:

- **4.2.7(a).** A remote-desktop app that mirrors "specific software or services" may only connect to a
  user-owned "personal computer or dedicated game console", with both on "a local and LAN-based network".
  There is an argument that Ripcord mirrors the whole console and so is a generic mirror outside this
  clause. But the clause names game consoles, so expect reviewers to read it as applying. **Internet play
  is what it targets.**
- **5.2.2.** An app using a third-party service must be "specifically permitted to do so under the
  service's terms of use", with "authorization … provided upon request". Sign-in, the console list and
  cloud wake all use PSN, and the project cannot produce such an authorization.
- **5.2.1.** Apps "should be submitted by the person or legal entity that owns or has licensed" the rights
  involved. Submitting means the owner personally warrants those rights to Apple, which covers the bundled
  OAuth credential most of all and the interop constants too. Whatever `NOTICE` describes, Apple decides
  what the Store accepts. Because Store apps can only be
  updated through the Store (2.4.5(vii)), a takedown after an IP complaint would strand every user.

**The workable version is a second, reduced edition:** LAN only, built without the bundled OAuth
credential (`-p:BundleOAuthClient=false` already exists on the .NET side, and the Swift side needs the same
switch). That answers 4.2.7 and 5.2.2, and leaves 5.2.1 to cover only the interop constants.

## Order of work

1. **Promote the core.** *Done 2026-09-24.* `ports/common` moved to `libripcord/`. The host suite, a
   compile-everything check, a sanitizer run and the fuzz harnesses now run in CI, which had never run the
   core's own suite at all. See the journal.
2. **Two tracks in parallel:**
   - **The Mac engine spike.** The C core already streams on the LAN, so a Swift command-line tool can
     discover, pair, connect and dump frames from a real console now. It also measures the crypto on
     Apple Silicon and settles the threading question.
   - **Take the core to parity.** Port STUN, the internet-play connect sequence and account pairing. Each
     gets `ProtocolLab` vectors and host tests first, and is then driven on hardware from the spike tool.
3. **The Swift cloud tier and the session actor**, ported from `Ripcord.Cloud.Halyard` and
   `SessionController`.
4. **First picture.** Deliberately plain: video, audio and input, with latency measured against the
   Windows figure of 18 ms from demux to present. *Written 2026-09-26; the latency has not met a console.*
5. **Design before building.** `DESIGN.md`, the Icon Composer icon (the brand's layers were drawn for it,
   see [`../brand/README.md`](../brand/README.md)), and prototypes of the launch, stream and pairing
   moments. *Done 2026-09-26:* [`../src/Ripcord.Mac/DESIGN.md`](../src/Ripcord.Mac/DESIGN.md) settles the
   hypotheses above, and the icon is generated.
6. **Library, pairing, Settings.** *Written 2026-09-26.*
7. **The stream experience.** Full screen, the inspector, PiP, recording, HDR, pointer and keyboard capture.
   *Written 2026-09-26. Pointer input is not sent: it has not been derived.*
8. **Present across the Mac.** Menu bar, widgets, Controls, App Intents. *Written 2026-09-26. The widget's
   data needs a signed build.*
9. **Ship.** Accessibility (VoiceOver, Reduce Motion, Reduce Transparency, Increase Contrast), String
   Catalogs, notarization, and a versioned release on its own `macos-v*` tag line. *Written 2026-09-26,
   without the signing secrets the release needs.*

"Written" is not "working": steps 4 and 6–9 were written and built together, and have not been run
against a console, launched for review, or released. What each still has to show is in
[`../ROADMAP.md`](../ROADMAP.md).
