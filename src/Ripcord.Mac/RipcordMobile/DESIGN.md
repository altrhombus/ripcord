# Ripcord for iPhone, iPad and Apple TV — design

Settled 2026-09-27, as step 3 of [`../../../docs/ios-plan.md`](../../../docs/ios-plan.md), before the surfaces are
built. It follows [`../DESIGN.md`](../DESIGN.md), the Mac's, and inherits everything that one says unless this
file says otherwise. Where they disagree, this file wins on these devices, and says why.

Nothing here has been tried on a device. Where a choice rests on how a platform behaves rather than on a
preference, it is marked `[X]` until a device settles it.

## The position

The same principle holds: **borrow the OS's behaviour, and spend identity only where the surface is Ripcord's
alone.** These devices supply even more than the Mac:
- **iPhone and iPad:** navigation, sheets, detents, the zoom transition, Picture in Picture, Liquid Glass, and
  a virtual controller.
- **Apple TV:** the focus engine, with its parallax.

The identity budget is the same three places: the tile, the launch, and the pairing celebration.

**One design across three form factors, not three designs.** iPhone, iPad and Apple TV share the model, the
surfaces and the words. What differs is how the same surface fits its screen and its input: touch, a pointer
and keyboard on iPad, and focus and a remote on the TV. Each section below says where they part.

## The mark, the palette, the icon

Unchanged: no fourth hue, and no `AccentColor`. The system accent tints controls, and the PlayStation blue appears
only where it means PlayStation.

- **iPhone and iPad** use the Mac's Icon Composer bundle, declared for iOS as well.
- **Apple TV** has its own layered icon: the ground behind, the mark in front, so the focus parallax lifts the
  mark off the ground as the brand intends. It also has a Top Shelf banner of the ground and the lockup. All of
  it is generated from the brand SVGs (`brand/README.md`).

## The library

- **iPhone.** A list in portrait, one console to a row, with the tile's content: the name, the family label, the
  state in words, and the wedge as the row's action. A grid of tiles wastes a phone's width. The state rules
  are the Mac's: Ready, Resting, Not found, never red, and Not found keeps the play action.
- **iPad.** The Mac's grid of tiles, the gradient rule included, at the width the window has. Stage Manager and
  Split View resize it like the Mac's window.
- **Apple TV.** A row of large tiles, each a focusable card. The system gives a focused card its lift and
  parallax, so the tile needs no hover or focus drawing of its own. Hover is not a thing on the TV. The focus
  is the system's, and it is correct in every accessibility setting for free.
- **Nothing paired** is the Mac's first-run surface on every device: the mark, one sentence, one action, and the
  same-network limit stated before anyone meets it.

**Opening a console** is a tap on iPhone and iPad, and a click on the TV. The context menu (a long press, or a
long click on the remote) holds Connect, Wake, Rename, Details and Forget, as on the Mac.

## The launch

**The tile becomes the stream,** as on the Mac.
- **iPhone and iPad:** the system's zoom transition from the tile.
- **Apple TV:** the tile's own lift, then a fade.
- **Reduce Motion:** a fade everywhere.

The connect overlay is the Mac's: the mark low and left with its three dashes filling at the engine's three
stages, one status line, and failure and reconnect in the same place. It moves to a folder the apps share when
it is built, rather than being redrawn.

**iPhone turns to landscape for the stream.** The picture is 16:9, and a phone held upright would show it at a
third of the screen. The library stays in whatever orientation the person holds it.

## The stream

**The picture is aspect-fit on black and owns the screen.** There is no chrome until someone asks for it.

- **iPhone and iPad:** a tap on the picture shows a glass capsule, with the console name, Picture in Picture, the
  inspector and Disconnect. It leaves on its own after three seconds, as the Mac's does.
  - **A pointer on iPad** shows the capsule the way the Mac's pointer does.
  - **The tap is not sent to the console.** The console's touchpad is a separate question, below.
- **Apple TV:** the remote's Back button shows the capsule, and a second Back disconnects, after asking. With a
  controller in hand, the remote is the way out, and the controller's buttons all belong to the console.
- **The inspector is the ladder,** as on the Mac. SwiftUI's inspector already becomes a sheet with detents on an
  iPhone and a trailing column on an iPad, which is the Mac's "rung 3 goes where the video isn't" arrived at
  by the platform. On the TV it is a side overlay, raised from the capsule.

### Controls

- **A controller is the first-class input on every device.** It goes to the console, all of it, as on the Mac.
  - **The PS button is the hard case.** The system claims a controller's home button for its own overlay, and
    whether an app can take it back is `[X]`. GameController has a per-element system-gesture preference that
    is understood to allow it, to confirm on a device. If it cannot, the capsule carries a PS button, and so
    does the watch.
- **Touch controls on iPhone and iPad borrow the OS: Apple's virtual controller** (`GCVirtualController`).
  - **Why borrow it:** it draws a controller over the picture and reports as an extended gamepad, so the
    InputHub takes it with no code of its own.
  - **What it lacks:** which elements it offers, and whether it covers Create, Options, the touchpad click
    and PS, is `[X]`. What it lacks, the capsule's row of console buttons supplies.
  - **A drawn, Ripcord-shaped overlay is not planned.** It would be the one place the app draws controls a
    platform already provides.
  - **Its size and placement** follow the system's, and it shows only while no physical controller is
    connected.
- **The screen as the console's touchpad** is not designed yet, because touchpad drag on the wire has not been
  derived (ROADMAP, controller). Until it has, the picture takes no touches but the one that shows the capsule.
- **A hardware keyboard on iPad** plays as the Mac's does: only while captured. Capture turns on with a tap on the
  picture while a keyboard is attached, and turns off with the same release chord, the default being ⌃⌥ held
  together.

### Picture in Picture and the background

**iPhone and iPad keep a stream through Picture in Picture.** Leaving the app floats the picture, as a video
app's does, and a controller keeps playing. Whether the session survives in the background that way is `[X]`,
the plan's open question. If it does not, leaving the app ends the stream, and the library says so rather
than showing a reconnect. Apple TV has no background to speak of.

## Pairing

The Mac's rule stands: **sign-in leads, and the code route is one press away.** The route is a mechanism, and the
player has no stake in it.

- **iPhone and iPad** sign in through the same web view as the Mac, in UIKit's presentation. The code form has a
  **scan the code** action, reading the TV through the phone's own camera with the same `PairingCodeReader` the
  Mac uses, with no Continuity Camera needed.
- **Apple TV has no web view, and typing a 19-digit account id with a remote is a chore.** So its pairing leans
  on the person's other devices:
  - **Pairings that travel with the person.** A console paired on their iPhone or Mac appears on their Apple
    TV. This is the recommendation, through iCloud Keychain.
    - **What it costs:** the registration keys, which are credentials for that console, leave the device.
      They go end-to-end encrypted, to the person's own devices only.
    - **The decision:** that is the person's to make, and the project's to make first. It is a setting,
      off until they turn it on.
  - **The code route with the remote** remains, for a TV with no other Apple device beside it. The account id
    is remembered once typed.
- **The celebration** is the Mac's: "Paired.", Play Now and Done as peers.

## Settings

An in-app Settings screen: a stock grouped form, in the Mac's pane order (Picture, Controls, Playing, Account,
Advanced). On iPhone and iPad it is a sheet from the library, and on the TV a screen of its own. The TV's
Controls pane has one extra: what the remote's buttons do during a stream.

## Present across the device

These are later work, settled now so the model stores what they need, as the Mac's section did.

- **iPhone and iPad:**
  - **Widgets** on the Home and Lock Screens, from the same snapshot the Mac's widget reads.
  - **A Control Center control**, "Connect to *console*".
  - **The App Intents** the Mac has, for Siri, Shortcuts and Spotlight.
- **Apple TV:** consoles on the **Top Shelf**. A Top Shelf extension lists the paired consoles when Ripcord is in
  the top row, and a click starts a stream. The extension needs the same shared snapshot.
- **Apple Watch:** the remote in `ios-plan.md`, with PS, Options and Create during a stream on the iPhone,
  wake, and state at a glance. It has the same look as the Mac's menu bar extra: the one-colour mark and plain
  rows.

## Accessibility

- **Text size.** iPhone and iPad follow Dynamic Type, all the way. The tile grows rather than truncates, and the
  iPhone's list is chosen partly because it grows well. The TV follows its own text size.
- **VoiceOver:**
  - a tile reads as one element: its name, family and state;
  - connect progress is announced once per stage;
  - the capsule stays up while VoiceOver runs, as the Mac's does.
- **Reduce Motion:** fades in place of the zoom, and the dashes appear rather than grow.
- **Reduce Transparency and Increase Contrast:** the system's glass handles both, as on the Mac.
- **Switch Control and Voice Control** can reach every surface but the stream, whose input is the console's.
  Saying so plainly is part of the design; claiming more is not.

## Deliberately not designed

- The sign-in page is not ours.
- The virtual controller is Apple's.
- The About screen is stock.
- iPhone's own touch-first game UI is out of scope. This is a remote-play client for a console, not a mobile game.
