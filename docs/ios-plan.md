# Ripcord for iPhone, iPad, Apple TV and Apple Watch — the plan

Started 2026-09-27, when the scaffolding went in. `engine-plan.md` said each later platform would get a plan file
in the shape of `macos-plan.md` when it started; this is iPhone and iPad's, and Apple TV and Apple Watch's with
them, because all four sit on the same Swift layer.

`[X]` here means what it means in the protocol docs: assumed and never confirmed. Most of the platform facts
below are that, because nothing has run on a device yet.

## The position

**One Apple codebase over one engine.** The Mac client already reaches the Rust engine through RipcordKit, and
RipcordKit's code turned out to be almost free of AppKit. So the other Apple platforms are the same framework
built for more SDKs, plus an app per form factor, not a port.

- **RipcordKit** is one framework for macOS, iOS and tvOS. The only macOS-only file is the sign-in window, which
  is AppKit's.
- **RipcordAppLogic** holds what the apps share that needs no window, compiled into each app and the tests. The
  health verdict, the network watch, settings, the audio output and the video hand-off are all used by the
  mobile app already.
- **The engine** is built for each SDK by the same Xcode phase, into `engine/target/shipping/<rust-target>`, and
  linked by an SDK-conditional setting. The XCFramework `engine-plan.md` describes becomes worth it when
  something outside this project links the engine. Until then, per-SDK builds do the same job with less.

## What the scaffolding is (2026-09-27)

`src/Ripcord.Mac/RipcordMobile/`, one target for iPhone, iPad and Apple TV:

- the paired consoles, in the Keychain as on the Mac;
- pairing with the code the console shows;
- a stream: the display layer, sound through the playback audio session, a controller through the same
  InputHub, the passcode prompt, and waking a resting console first.

It builds in Debug and Release for iOS and tvOS, device and simulator, and CI builds it. **It has not run on
any of them.** Device builds are unsigned, because iOS and tvOS refuse an ad hoc signature. Installing one
needs a team and a profile. The icon is the Mac's Icon Composer bundle declared for iOS as well, compiled with
`actool` and looked at. Apple TV needs a different kind of icon, still to do.

## Decisions

| Question | Decision |
|---|---|
| One app target or several | One, for iPhone, iPad and Apple TV, with `#if os(tvOS)` where the platforms differ. Split it if the TV app diverges enough to make the conditionals the larger part |
| Bundle id | The Mac app's, so the platforms can one day be one purchase |
| Minimum versions | iOS, iPadOS and tvOS 26, as the Mac is 26 |
| Controller | Required for now: a Bluetooth pad on iPhone and iPad, and the pad an Apple TV has. Touch controls are later work, not scaffolding |
| Pairing | The code route now. Sign-in pairing on iPhone and iPad is the same web view as the Mac's, in UIKit's presentation. Apple TV has no WebKit, so it needs another route (below) |

## Open questions, to settle by measurement

- **Local network.** The first connect should raise the Local Network prompt; nothing works without it.
  - **Broadcast discovery** is expected to need Apple's multicast networking entitlement on iOS, which Apple
    grants on request `[X]`.
  - **Without it**, discovery works only by unicast to known addresses, which the network watch already does
    first. The pairing screen then has to take an address.
  - **Wake** is one unicast datagram, so it should be unaffected `[X]`.
- **Background.** iOS suspends an app shortly after it leaves the screen, so a stream ends when a person
  switches away.
  - **Picture in Picture** from the sample-buffer display layer should keep one alive `[X]`. The Mac already
    uses that layer.
- **Apple TV input.**
  - **The Siri Remote** reports as a micro gamepad, which the InputHub ignores, since it reads only extended
    gamepads. So the remote drives the app's own menus and a real controller plays.
  - **Info.plist keys.** Whether tvOS or the App Store wants a key declaring controller support is `[X]`. The
    SDK headers name none, so read Apple's current documentation before adding one.
- **Apple TV pairing.** With no WebKit there is no web sign-in. The candidates:
  - **the code route**, typed with the remote;
  - **a pairing handed over from the iPhone**, through a shared Keychain access group or iCloud Keychain. Either
    needs a team, and both need a decision about what leaves the device.
- **Crypto and decode throughput** on an iPhone, and the latency beside the Mac's.

## Distribution: the real constraint

**On iPhone, iPad and Apple TV, the App Store is effectively the only route**, and TestFlight goes through a
lighter review of its own.

- **The App Store analysis.** `macos-plan.md`'s "The App Store" section applies unchanged. Guideline 4.2.7
  names consoles, 5.2.2 needs PSN's permission, and 5.2.1 makes the submitter warrant the rights, the bundled
  OAuth credential above all.
- **What that means for the Mac.** There, the Store was "not the primary release, and not ruled out".
- **What it means here.** The Store is the release, so its answer decides whether these apps ship at all.
- **The candidate** is the same reduced edition: LAN only, built without the OAuth credential. That wants
  counsel's review, as `NOTICE` advises, before anything is submitted.
- **Other routes.** The EU's alternative marketplaces and side-loading are `[X]`, not researched.

## Apple Watch: a remote, not a client

**The watch will not stream.** It is not the screen for a game, and its low-level networking is understood to
be restricted to a few kinds of app, so it could not run the engine's sockets even if it could decode
`[X]`, from Apple's watchOS networking guidance, to read before anything relies on it.

What it can usefully be is a remote for the iPhone that is streaming, or about to:

- **A few buttons during a stream on the iPhone:** PS, Options and Create, and perhaps rest-the-console. The
  press goes over WatchConnectivity to the phone, which puts it on the live session as the pad would. This is
  the "button while using another device" idea, and the one with a clear use: the PS button is awkward on a
  phone with a clip-on controller, and useful to have on the wrist.
- **Wake or rest a console**, by asking the phone to send the datagram. Whether the phone app can be woken in
  the background to do it is `[X]`.
- **A console's state** in the Smart Stack or a complication, from a snapshot the phone shares, as the Mac's
  widget does.

**The Mac is out of reach here.** WatchConnectivity pairs a watch with its iPhone only. A watch-to-Mac remote
would need a route through iCloud or the network, which is slower and a separate decision.

The watch gets a target when its first feature is built, not before. A target that does nothing is not
scaffolding, just something to keep building.

## Order of work

1. **Scaffolding.** *Done 2026-09-27.* RipcordKit and the engine on iOS and tvOS, the one app target, CI.
2. **First run on hardware:** an iPhone and an Apple TV on the LAN, pairing by code, a stream. It settles the
   Local Network prompt, the multicast question and the throughput.
3. **The design pass:** `DESIGN.md` for touch and for the TV's focus, in the Mac's shape, before building
   surfaces.
4. **Touch controls,** for a phone with no controller.
5. **Sign-in on iPhone and iPad,** and Apple TV's pairing route.
6. **Picture in Picture and background.**
7. **The watch remote.**
8. **Distribution,** after counsel, and the reduced edition's build.
